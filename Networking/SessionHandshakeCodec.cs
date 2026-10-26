using System;
using System.IO;
using System.Text;

namespace ULTRAKILL.Bloodlink.Networking
{
    public sealed class SessionHelloData
    {
        public string PlayerName { get; private set; }
        public string SessionToken { get; private set; }

        internal SessionHelloData(string playerName, string sessionToken)
        {
            PlayerName = playerName;
            SessionToken = sessionToken;
        }
    }

    public static class SessionHandshakeCodec
    {
        public const int SessionTokenHexLength = 64;
        private const int MaximumPlayerNameBytes = 96;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public static byte[] EncodeHello(string playerName, string sessionToken)
        {
            ValidateToken(sessionToken);
            if (string.IsNullOrWhiteSpace(playerName)) throw new ArgumentException("Player name is required.", nameof(playerName));
            var nameBytes = StrictUtf8.GetBytes(playerName.Trim());
            if (nameBytes.Length == 0 || nameBytes.Length > MaximumPlayerNameBytes)
                throw new ArgumentOutOfRangeException(nameof(playerName));
            var tokenBytes = Encoding.ASCII.GetBytes(sessionToken);
            using (var stream = new MemoryStream(2 + nameBytes.Length + tokenBytes.Length))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)nameBytes.Length);
                writer.Write(nameBytes);
                writer.Write((byte)tokenBytes.Length);
                writer.Write(tokenBytes);
                return stream.ToArray();
            }
        }

        public static SessionHelloData DecodeHello(byte[] payload)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            using (var stream = new MemoryStream(payload, false))
            using (var reader = new BinaryReader(stream))
            {
                if (stream.Length < 2) throw new InvalidDataException("Session hello is too short.");
                var nameLength = reader.ReadByte();
                if (nameLength == 0 || nameLength > MaximumPlayerNameBytes || stream.Length - stream.Position < nameLength + 1)
                    throw new InvalidDataException("Invalid player name length in session hello.");
                string name;
                try { name = StrictUtf8.GetString(reader.ReadBytes(nameLength)); }
                catch (DecoderFallbackException ex) { throw new InvalidDataException("Invalid player name encoding in session hello.", ex); }
                var tokenLength = reader.ReadByte();
                if (tokenLength != SessionTokenHexLength || stream.Length - stream.Position != tokenLength)
                    throw new InvalidDataException("Invalid session token length.");
                var token = Encoding.ASCII.GetString(reader.ReadBytes(tokenLength));
                try { ValidateToken(token); }
                catch (ArgumentException ex) { throw new InvalidDataException("Invalid session token.", ex); }
                if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("Player name is empty.");
                return new SessionHelloData(name, token.ToLowerInvariant());
            }
        }

        public static void ValidateToken(string token)
        {
            if (token == null || token.Length != SessionTokenHexLength)
                throw new ArgumentException("Session token must contain 64 hexadecimal characters.", nameof(token));
            for (var i = 0; i < token.Length; i++)
                if (!Uri.IsHexDigit(token[i])) throw new ArgumentException("Session token must contain only hexadecimal characters.", nameof(token));
        }
    }
}
