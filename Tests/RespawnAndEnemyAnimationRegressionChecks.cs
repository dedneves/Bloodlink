using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using ULTRAKILL.Bloodlink.Networking;
using UnityEngine;

internal static partial class Program
{
    private static void EnemySnapshotWireRegressionChecks()
    {
        // Exercise the production snapshot writer/reader, including its numeric
        // sanitization, rather than a second implementation in this test runner.
        var pluginPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "bin", "Release", "netstandard2.1", "ULTRAKILL.Bloodlink.dll"));
        var assembly = System.Reflection.Assembly.LoadFrom(pluginPath);
        var stateType = assembly.GetType("ULTRAKILL.Bloodlink.Enemies.EnemyWireState", true);
        var registryType = assembly.GetType("ULTRAKILL.Bloodlink.Enemies.EnemySyncRegistry", true);
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        var write = registryType.GetMethod("WriteState", flags);
        var read = registryType.GetMethod("ReadState", flags);
        var state = Activator.CreateInstance(stateType, true);
        Action<string, object> set = (name, value) => stateType.GetField(name).SetValue(state, value);
        set("NetworkId", 41u);
        set("StableKey", 53u);
        set("Type", Enum.GetValues(stateType.GetField("Type").FieldType).GetValue(0));
        set("Position", new Vector3(1, 2, 3));
        set("Rotation", Quaternion.identity);
        set("Health", -10f);
        set("Dead", true);
        set("Active", true);
        set("Velocity", new Vector3(float.PositiveInfinity, 0, 0));
        set("AnimationNormalizedTime", 1f);
        set("AnimationSpeed", 0.75f);
        using (var stream = new MemoryStream())
        {
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true)) write.Invoke(null, new[] { writer, state });
            stream.Position = 0;
            using (var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true))
            {
                var result = read.Invoke(null, new object[] { reader, 123u });
                Assert((uint)stateType.GetField("NetworkId").GetValue(result) == 41u &&
                    (uint)stateType.GetField("Frame").GetValue(result) == 123u,
                    "production enemy snapshot preserves entity identity and frame");
                Assert((float)stateType.GetField("Health").GetValue(result) == 0f &&
                    (bool)stateType.GetField("Dead").GetValue(result),
                    "negative native death health becomes a valid dead snapshot instead of dropping the whole batch");
                Assert(((Vector3)stateType.GetField("Velocity").GetValue(result)).sqrMagnitude == 0f &&
                    (float)stateType.GetField("AnimationNormalizedTime").GetValue(result) == 0f,
                    "non-finite enemy velocity and rounded animation phase cannot poison a full resync packet");
                Assert((float)stateType.GetField("AnimationSpeed").GetValue(result) == 0.75f && stream.Position == stream.Length,
                    "production reader consumes the new animation payload and retains playback speed");
            }
        }
    }

    private static void EnemyAnimatorParameterChecks()
    {
        var values = new[] {
            new EnemyAnimatorParameter { Hash = 12, Type = 1, Value = 3.25f },
            new EnemyAnimatorParameter { Hash = -42, Type = 3, Value = -7 },
            new EnemyAnimatorParameter { Hash = 91, Type = 4, Value = 1 }
        };
        byte[] payload;
        using (var stream = new MemoryStream())
        {
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true)) EnemyAnimatorParameterCodec.Write(writer, values);
            payload = stream.ToArray();
        }
        using (var stream = new MemoryStream(payload))
        using (var reader = new BinaryReader(stream))
        {
            var decoded = EnemyAnimatorParameterCodec.Read(reader);
            Assert(decoded.Length == 3 && decoded[0].Hash == 12 && decoded[0].Value == 3.25f &&
                decoded[1].Type == 3 && decoded[1].Value == -7 && decoded[2].Type == 4 && decoded[2].Value == 1,
                "enemy animator float, integer and boolean parameters survive network serialization");
            Assert(stream.Position == stream.Length, "animator parameter decoder consumes precisely its record");
        }
        foreach (var invalid in new[] {
            new EnemyAnimatorParameter { Type = 1, Value = float.NaN },
            new EnemyAnimatorParameter { Type = 1, Value = float.PositiveInfinity },
            new EnemyAnimatorParameter { Type = 9, Value = 1 },
            new EnemyAnimatorParameter { Type = 4, Value = 2 },
            new EnemyAnimatorParameter { Type = 3, Value = 1.5f },
            new EnemyAnimatorParameter { Type = 3, Value = 2147483648f }
        })
        {
            var rejected = false;
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
                try { EnemyAnimatorParameterCodec.Write(writer, new[] { invalid }); }
                catch (InvalidDataException) { rejected = true; }
            Assert(rejected, "invalid animation parameter cannot poison an enemy snapshot batch");
        }
        var excessive = false;
        using (var reader = new BinaryReader(new MemoryStream(new byte[] { 255 })))
            try { EnemyAnimatorParameterCodec.Read(reader); } catch (InvalidDataException) { excessive = true; }
        Assert(excessive, "animation parameter count is bounded before allocating or reading attacker data");
        var truncated = false;
        using (var reader = new BinaryReader(new MemoryStream(new byte[] { 1, 0 })))
            try { EnemyAnimatorParameterCodec.Read(reader); } catch (EndOfStreamException) { truncated = true; }
        Assert(truncated, "truncated animation state is rejected");
    }

    private static void RespawnMovementNetworkChecks()
    {
        int port;
        using (var probe = new UdpClient(0)) port = ((IPEndPoint)probe.Client.LocalEndPoint).Port;
        using (var host = new NetworkService())
        using (var client = new NetworkService())
        {
            var position = Vector3.zero;
            Action pump = () => {
                host.Update(0.01f, Vector3.zero, Quaternion.identity, Vector3.zero);
                client.Update(0.01f, position, Quaternion.identity, Vector3.zero);
                Thread.Sleep(5);
            };
            Action<Func<bool>, string> until = (condition, message) => {
                for (var i = 0; i < 500 && !condition(); i++) pump();
                Assert(condition(), message);
            };
            host.StartHost(port, 2, "Host", bindAddress: "127.0.0.1");
            client.StartClient("127.0.0.1", port, "Client");
            until(() => client.IsConnected && host.Players.ContainsKey(client.LocalPlayerId), "respawn test client joins host");
            var id = client.LocalPlayerId;
            until(() => host.HasFreshPlayerSnapshot(id, 0.5f), "host has a fresh client position before death");
            Assert(host.SetAuthoritativePlayerVitals(id, 0, true), "host commits client death");
            until(() => client.Players.ContainsKey(id) && (client.Players[id].MovementFlags & 4) != 0, "reliable death reaches client");
            Assert(host.SetAuthoritativePlayerVitals(id, 100, false), "host authorizes client revival");
            until(() => (client.Players[id].MovementFlags & 4) == 0, "revival reaches client without reconnecting or changing scene");
            position = new Vector3(1000, 2, 0);
            until(() => (host.Players[id].Position - position).sqrMagnitude < 0.01f,
                "checkpoint teleport after respawn is accepted instead of clamped back to the corpse");
            for (var i = 0; i < 220; i++) pump();
            position = new Vector3(3000, 2, 0);
            for (var i = 0; i < 12; i++) pump();
            Assert(host.Players[id].Position.x < 2000f, "ordinary movement limits resume after the respawn grace window");
            host.AllowPlayerReposition(id);
            until(() => (host.Players[id].Position - position).sqrMagnitude < 0.01f,
                "host-authorized arena teleport is accepted without being dragged back by movement reconciliation");
            Assert(host.SceneSnapshotSyncReady && client.SceneSnapshotSyncReady,
                "arena reposition does not reopen the level loading or READY barrier");
        }
    }
}
