using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using ULTRAKILL.Bloodlink.Networking;

namespace ULTRAKILL.Bloodlink.Levels
{
    // Scene identities are assigned before gameplay mutates the hierarchy and are
    // retained when objects are reparented, collected, or their siblings disappear.
    internal static class SceneObjectIdentity
    {
        private sealed class Entry { public Transform Transform; public string Scene; public string Path; }
        private static readonly Dictionary<int, Entry> byInstance = new Dictionary<int, Entry>();
        private static readonly Dictionary<string, Entry> byPath = new Dictionary<string, Entry>(StringComparer.Ordinal);

        internal static bool IsCurrentScene(string identity)
        {
            var active = SceneManager.GetActiveScene();
            return active.IsValid() && LevelSceneReadinessPolicy.MatchesCurrentScene(identity,
                SceneHelper.CurrentScene, active.name);
        }

        internal static void PrimeScene(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded) return;
            var removed = new List<int>();
            foreach (var pair in byInstance) if (pair.Value.Scene == scene.name || pair.Value.Transform == null) removed.Add(pair.Key);
            foreach (var id in removed)
            {
                var entry = byInstance[id];
                byPath.Remove(entry.Scene + "|" + entry.Path);
                byInstance.Remove(id);
            }
            var roots = scene.GetRootGameObjects();
            var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var root in roots)
            {
                int ordinal; occurrences.TryGetValue(root.name, out ordinal);
                occurrences[root.name] = ordinal + 1;
                RegisterTree(root.transform, scene.name, StableHierarchyPath.EncodeSegment(root.name, ordinal));
            }
        }

        private static void RegisterTree(Transform target, string scene, string path)
        {
            Register(target, scene, path);
            var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < target.childCount; i++)
            {
                var child = target.GetChild(i);
                int ordinal; occurrences.TryGetValue(child.name, out ordinal);
                occurrences[child.name] = ordinal + 1;
                RegisterTree(child, scene, path + "/" + StableHierarchyPath.EncodeSegment(child.name, ordinal));
            }
        }

        private static void Register(Transform target, string scene, string path)
        {
            var entry = new Entry { Transform = target, Scene = scene, Path = path };
            byInstance[target.GetInstanceID()] = entry;
            byPath[scene + "|" + path] = entry;
        }

        internal static void AssignPath(Transform target, string scene, string path) => Register(target, scene, path);

        internal static bool HasPrimedIdentity(Transform target)
        {
            if (target == null) return false;
            Entry entry;
            return byInstance.TryGetValue(target.GetInstanceID(), out entry) && entry.Transform == target;
        }

        internal static bool TryBuild<T>(T component, out string sceneName, out string path, out byte componentIndex) where T : Component
        {
            sceneName = null; path = null; componentIndex = 0;
            if (component == null || !component.gameObject.scene.IsValid()) return false;
            sceneName = component.gameObject.scene.name;
            if (!TryBuildPath(component.transform, out path)) return false;
            Entry identity;
            if (byInstance.TryGetValue(component.transform.GetInstanceID(), out identity)) sceneName = identity.Scene;
            // Callers typed as Component must not count unrelated/runtime components.
            var components = component.GetComponents(component.GetType());
            var found = Array.IndexOf(components, component);
            if (found < 0 || found > byte.MaxValue) return false;
            componentIndex = (byte)found;
            return true;
        }

        internal static bool TryBuildPath(Transform target, out string path)
        {
            path = null;
            if (target == null || !target.gameObject.scene.IsValid()) return false;
            Entry known;
            if (byInstance.TryGetValue(target.GetInstanceID(), out known) && known.Transform == target)
            { path = known.Path; return true; }
            var segments = new Stack<string>();
            for (var item = target; item != null; item = item.parent)
            {
                var ordinal = 0;
                if (item.parent != null)
                {
                    for (var i = 0; i < item.GetSiblingIndex(); i++) if (item.parent.GetChild(i).name == item.name) ordinal++;
                }
                else
                {
                    foreach (var root in item.gameObject.scene.GetRootGameObjects())
                    { if (root.transform == item) break; if (root.name == item.name) ordinal++; }
                }
                segments.Push(StableHierarchyPath.EncodeSegment(item.name, ordinal));
            }
            path = string.Join("/", segments.ToArray());
            Register(target, target.gameObject.scene.name, path);
            return true;
        }

        internal static uint GetStableKey(Component component)
        {
            string scene, path; byte index;
            return TryBuild(component, out scene, out path, out index) ? HashStableIdentity(scene, path + "@" + component.GetType().FullName, index) : 0;
        }

        internal static uint GetStableTransformKey(Transform target)
        {
            string path;
            if (target == null || !TryBuildPath(target, out path)) return 0;
            Entry identity;
            return HashStableIdentity(byInstance.TryGetValue(target.GetInstanceID(), out identity) ? identity.Scene : target.gameObject.scene.name, path, 0);
        }

        internal static uint HashStableIdentity(string scene, string path, byte index)
        {
            if (string.IsNullOrEmpty(scene) || string.IsNullOrEmpty(path)) return 0;
            var bytes = Encoding.UTF8.GetBytes(scene + "/" + path + "#" + index);
            unchecked { uint hash = 2166136261; foreach (var value in bytes) { hash ^= value; hash *= 16777619; } return hash == 0 ? 1u : hash; }
        }

        internal static T Resolve<T>(string sceneName, string path, byte index) where T : Component
        {
            if (string.IsNullOrEmpty(sceneName) || string.IsNullOrEmpty(path)) return null;
            Entry entry;
            if (byPath.TryGetValue(sceneName + "|" + path, out entry))
            {
                if (entry.Transform == null) return null;
                var values = entry.Transform.GetComponents<T>();
                return index < values.Length ? values[index] : null;
            }
            var scene = SceneManager.GetSceneByName(sceneName);
            if (!scene.IsValid() && IsCurrentScene(sceneName)) scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded) return null;
            Transform current = null;
            foreach (var segment in path.Split('/'))
            {
                string name; int ordinal;
                if (!StableHierarchyPath.TryDecodeSegment(segment, out name, out ordinal)) return null;
                var seen = 0;
                Transform found = null;
                if (current == null)
                {
                    foreach (var root in scene.GetRootGameObjects()) if (root.name == name && seen++ == ordinal) { found = root.transform; break; }
                }
                else for (var i = 0; i < current.childCount; i++)
                { var child = current.GetChild(i); if (child.name == name && seen++ == ordinal) { found = child; break; } }
                if (found == null) return null;
                current = found;
            }
            if (current == null) return null;
            Register(current, sceneName, path);
            var components = current.GetComponents<T>();
            return index < components.Length ? components[index] : null;
        }

        internal static T ResolveNear<T>(string sceneName, string path, byte index, Vector3 position) where T : Component
        {
            var exact = Resolve<T>(sceneName, path, index);
            if (exact != null) return exact;
            Entry known;
            if (byPath.TryGetValue(sceneName + "|" + path, out known)) return null;
            var segment = path.Substring(path.LastIndexOf('/') + 1);
            string name; int ordinal;
            if (!StableHierarchyPath.TryDecodeSegment(segment, out name, out ordinal)) return null;
            T best = null; var distance = 1f;
            foreach (var candidate in Resources.FindObjectsOfTypeAll<T>())
            {
                if (candidate == null || candidate.name != name) continue;
                if (candidate.gameObject.scene.name != sceneName &&
                    !(IsCurrentScene(sceneName) && IsCurrentScene(candidate.gameObject.scene.name))) continue;
                var squared = (candidate.transform.position - position).sqrMagnitude;
                if (squared < distance) { distance = squared; best = candidate; }
            }
            return best;
        }
    }
}
