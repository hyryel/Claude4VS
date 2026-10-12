using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ClaudeVS.Chat
{
    /// <summary>Liste des fichiers du dossier de travail, pour l'autocomplétion des @-mentions.</summary>
    internal sealed class FileIndex
    {
        private static readonly HashSet<string> ExcludedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "obj", ".git", ".vs", ".idea", "node_modules", "packages", "TestResults", ".claude",
        };
        private const int MaxFiles = 50000;
        private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(30);

        private readonly object _gate = new object();
        private string _root;
        private string[] _files = new string[0];   // chemins relatifs, séparateur '/'
        private DateTime _builtAt = DateTime.MinValue;
        private Task _building;

        public async Task<IReadOnlyList<string>> SearchAsync(string root, string query, int max = 30)
        {
            Task building;
            lock (_gate)
            {
                var stale = !string.Equals(root, _root, StringComparison.OrdinalIgnoreCase) || DateTime.UtcNow - _builtAt > MaxAge;
                if (stale && (_building == null || _building.IsCompleted))
                    _building = Task.Run(() => Build(root));
                building = string.Equals(root, _root, StringComparison.OrdinalIgnoreCase) ? null : _building;
            }
            // Premier appel pour ce dossier : on attend l'index ; ensuite on répond avec l'ancien pendant le rafraîchissement
            if (building != null) await building;

            string[] files;
            lock (_gate) files = _files;
            return Rank(files, query ?? "", max);
        }

        private void Build(string root)
        {
            var result = new List<string>();
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0 && result.Count < MaxFiles)
            {
                var dir = stack.Pop();
                try
                {
                    foreach (var f in Directory.EnumerateFiles(dir))
                        result.Add(f.Substring(root.Length).TrimStart('\\').Replace('\\', '/'));
                    foreach (var d in Directory.EnumerateDirectories(dir))
                        if (!ExcludedDirs.Contains(Path.GetFileName(d))) stack.Push(d);
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
            result.Sort(StringComparer.OrdinalIgnoreCase);
            lock (_gate)
            {
                _root = root;
                _files = result.ToArray();
                _builtAt = DateTime.UtcNow;
            }
        }

        /// <summary>Nom de fichier qui commence par la requête &gt; nom qui la contient &gt; chemin qui la contient.</summary>
        private static IReadOnlyList<string> Rank(string[] files, string query, int max)
        {
            query = query.Replace('\\', '/');
            if (query.Length == 0)
                return files.Where(f => f.IndexOf('/') < 0).Take(max).ToList();

            return files
                .Select(f =>
                {
                    var name = f.Substring(f.LastIndexOf('/') + 1);
                    int score =
                        name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 :
                        name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ? 1 :
                        f.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ? 2 : -1;
                    return (f, score);
                })
                .Where(x => x.score >= 0)
                .OrderBy(x => x.score).ThenBy(x => x.f.Length).ThenBy(x => x.f, StringComparer.OrdinalIgnoreCase)
                .Take(max)
                .Select(x => x.f)
                .ToList();
        }
    }
}
