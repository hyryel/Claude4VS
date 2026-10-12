using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace ClaudeVS.Chat
{
    /// <summary>Conversations enregistrées par le CLI dans ~/.claude/projects/&lt;dossier encodé&gt;/*.jsonl.</summary>
    internal static class SessionStore
    {
        private const int MaxToolResultChars = 20000;

        private static string ProjectDir(string cwd)
        {
            var config = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (string.IsNullOrEmpty(config))
                config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
            return Path.Combine(config, "projects", Regex.Replace(cwd, "[^A-Za-z0-9]", "-"));
        }

        public static JArray List(string cwd, Log log)
        {
            var result = new JArray();
            var dir = ProjectDir(cwd);
            if (!Directory.Exists(dir)) return result;
            foreach (var file in new DirectoryInfo(dir).GetFiles("*.jsonl").OrderByDescending(f => f.LastWriteTimeUtc).Take(30))
            {
                var title = FirstPrompt(file.FullName, log);
                if (title == null) continue;
                result.Add(new JObject
                {
                    ["id"] = Path.GetFileNameWithoutExtension(file.Name),
                    ["title"] = title,
                    ["updated"] = file.LastWriteTime.ToString("dd/MM HH:mm"),
                });
            }
            return result;
        }

        private static string FirstPrompt(string file, Log log)
        {
            try
            {
                foreach (var line in ReadLines(file).Take(200))
                {
                    var o = TryParse(line);
                    if ((string)o?["type"] != "user" || (bool?)o["isMeta"] == true || (bool?)o["isSidechain"] == true) continue;
                    var text = UserText(o["message"]?["content"]);
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    text = text.Trim().Replace("\r", " ").Replace("\n", " ");
                    return text.Length > 90 ? text.Substring(0, 90) + "…" : text;
                }
            }
            catch (Exception ex) { log?.Info($"Session illisible {Path.GetFileName(file)} : {ex.Message}"); }
            return null;
        }

        /// <summary>Texte saisi par l'utilisateur, sans les blocs de contexte IDE ni les résultats d'outils.</summary>
        private static string UserText(JToken content)
        {
            if (content is JValue v) return Clean((string)v);
            if (content is JArray arr)
                return string.Join("\n", arr.Where(b => (string)b["type"] == "text").Select(b => Clean((string)b["text"])).Where(t => !string.IsNullOrWhiteSpace(t)));
            return null;
        }

        private static string Clean(string t)
        {
            if (t == null) return null;
            if (t.StartsWith("<ide_") || t.StartsWith("<command-") || t.StartsWith("<local-command") || t.StartsWith("<system-reminder")) return null;
            return t;
        }

        /// <summary>Messages user/assistant de la session, résultats d'outils volumineux tronqués (le panneau n'en affiche que 4 000 caractères).</summary>
        public static JArray Load(string cwd, string sessionId, Log log)
        {
            var result = new JArray();
            var file = Path.Combine(ProjectDir(cwd), sessionId + ".jsonl");
            if (!File.Exists(file)) return result;
            try
            {
                foreach (var line in ReadLines(file))
                {
                    var o = TryParse(line);
                    var type = (string)o?["type"];
                    if ((type != "user" && type != "assistant") || (bool?)o["isSidechain"] == true || (bool?)o["isMeta"] == true) continue;
                    if (type == "user") TruncateToolResults(o["message"]?["content"] as JArray);
                    result.Add(new JObject { ["type"] = type, ["uuid"] = o["uuid"], ["message"] = o["message"] });
                }
            }
            catch (Exception ex) { log?.Error($"Lecture de la session {sessionId} : {ex.Message}"); }
            return result;
        }

        private static void TruncateToolResults(JArray content)
        {
            if (content == null) return;
            foreach (var block in content.OfType<JObject>())
            {
                if ((string)block["type"] != "tool_result") continue;
                if (block["content"] is JValue v && v.Type == JTokenType.String)
                    block["content"] = Truncate((string)v);
                else if (block["content"] is JArray parts)
                    foreach (var p in parts.OfType<JObject>())
                        if ((string)p["type"] == "text" && p["text"] is JValue t && t.Type == JTokenType.String) p["text"] = Truncate((string)t);
            }
        }

        private static string Truncate(string text) =>
            text != null && text.Length > MaxToolResultChars ? text.Substring(0, MaxToolResultChars) + "\n… [tronqué]" : text;

        private static IEnumerable<string> ReadLines(string file)
        {
            // Le CLI peut être en train d'écrire : partage en lecture/écriture
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs))
            {
                string line;
                while ((line = sr.ReadLine()) != null) yield return line;
            }
        }

        private static JObject TryParse(string line)
        {
            try { return JObject.Parse(line); } catch { return null; }
        }
    }
}
