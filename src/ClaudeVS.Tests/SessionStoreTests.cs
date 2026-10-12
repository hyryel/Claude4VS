using System;
using System.IO;
using System.Linq;
using ClaudeVS.Chat;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ClaudeVS.Tests
{
    // Une seule classe modifie CLAUDE_CONFIG_DIR : xUnit n'exécute pas ses tests en parallèle entre eux.
    public class SessionStoreTests : IDisposable
    {
        private const string Cwd = "C:\\dev\\mon app";                // -> C--dev-mon-app
        private readonly string _root = Path.Combine(Path.GetTempPath(), "ClaudeVS.Tests", Guid.NewGuid().ToString("N"));
        private readonly string _previous = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        private readonly string _dir;
        private readonly Log _log = new Log();

        public SessionStoreTests()
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", _root);
            _dir = Path.Combine(_root, "projects", "C--dev-mon-app");
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", _previous);
            try { Directory.Delete(_root, true); } catch { }
        }

        private void Session(string id, params string[] lines) => File.WriteAllLines(Path.Combine(_dir, id + ".jsonl"), lines);
        private static string User(object content, string extra = "") => "{\"type\":\"user\"," + extra + "\"uuid\":\"u\",\"message\":{\"role\":\"user\",\"content\":" + Newtonsoft.Json.JsonConvert.SerializeObject(content) + "}}";

        [Fact]
        public void List_dossier_inexistant_donne_liste_vide() => Assert.Empty(SessionStore.List("D:\\autre", _log));

        [Fact]
        public void List_retourne_id_et_titre_de_la_premiere_demande()
        {
            Session("abc", User("Bonjour Claude"), User("Deuxieme"));
            var l = SessionStore.List(Cwd, _log);
            Assert.Single(l);
            Assert.Equal("abc", (string)l[0]["id"]);
            Assert.Equal("Bonjour Claude", (string)l[0]["title"]);
        }

        [Fact]
        public void List_ignore_les_sessions_sans_prompt()
        {
            Session("vide", "{\"type\":\"summary\"}", "pas du json", User("   "));
            Session("ok", User("question"));
            Assert.Equal(new[] { "ok" }, SessionStore.List(Cwd, _log).Select(s => (string)s["id"]).ToArray());
        }

        [Fact]
        public void List_tronque_le_titre_a_90_caracteres()
        {
            Session("long", User(new string('x', 200)));
            var title = (string)SessionStore.List(Cwd, _log)[0]["title"];
            Assert.Equal(new string('x', 90) + "…", title);
        }

        [Fact]
        public void List_titre_de_90_caracteres_exactement_non_tronque()
        {
            Session("s", User(new string('x', 90)));
            Assert.Equal(new string('x', 90), (string)SessionStore.List(Cwd, _log)[0]["title"]);
        }

        [Fact]
        public void List_ignore_les_blocs_ide_et_prend_le_texte_suivant()
        {
            Session("s", User(new[] { new { type = "text", text = "<ide_opened_file>x</ide_opened_file>" }, new { type = "text", text = "ma vraie question" } }));
            Assert.Equal("ma vraie question", (string)SessionStore.List(Cwd, _log)[0]["title"]);
        }

        [Fact]
        public void List_ignore_meta_sidechain_et_messages_de_commande()
        {
            Session("s",
                User("meta", "\"isMeta\":true,"),
                User("side", "\"isSidechain\":true,"),
                User("<command-name>/clear</command-name>"),
                User("<system-reminder>x</system-reminder>"),
                User("la bonne"));
            Assert.Equal("la bonne", (string)SessionStore.List(Cwd, _log)[0]["title"]);
        }

        [Fact]
        public void List_remplace_les_retours_a_la_ligne_dans_le_titre()
        {
            Session("s", User("ligne1\r\nligne2\nligne3"));
            Assert.Equal("ligne1  ligne2 ligne3", (string)SessionStore.List(Cwd, _log)[0]["title"]);
        }

        [Fact]
        public void List_ne_garde_que_les_30_sessions_les_plus_recentes()
        {
            for (var i = 0; i < 35; i++) Session("s" + i, User("p" + i));
            Assert.Equal(30, SessionStore.List(Cwd, _log).Count);
        }

        [Fact]
        public void Load_session_inexistante_donne_liste_vide() => Assert.Empty(SessionStore.Load(Cwd, "nope", _log));

        [Fact]
        public void Load_ne_garde_que_user_et_assistant_hors_meta_et_sidechain()
        {
            Session("s",
                User("a"),
                "{\"type\":\"assistant\",\"uuid\":\"x\",\"message\":{\"role\":\"assistant\",\"content\":[]}}",
                "{\"type\":\"summary\"}",
                "pas du json",
                User("meta", "\"isMeta\":true,"),
                User("side", "\"isSidechain\":true,"));
            var r = SessionStore.Load(Cwd, "s", _log);
            Assert.Equal(new[] { "user", "assistant" }, r.Select(m => (string)m["type"]).ToArray());
            Assert.Equal("x", (string)r[1]["uuid"]);
        }

        [Fact]
        public void Load_tronque_les_tool_result_de_plus_de_20000_caracteres()
        {
            var big = new string('y', 20001);
            Session("s", User(new object[]
            {
                new { type = "tool_result", tool_use_id = "1", content = big },
                new { type = "tool_result", tool_use_id = "2", content = new[] { new { type = "text", text = big } } },
                new { type = "tool_result", tool_use_id = "3", content = new string('z', 20000) },
            }));
            var blocks = (JArray)SessionStore.Load(Cwd, "s", _log)[0]["message"]["content"];
            Assert.Equal(new string('y', 20000) + "\n… [tronqué]", (string)blocks[0]["content"]);
            Assert.Equal(new string('y', 20000) + "\n… [tronqué]", (string)blocks[1]["content"][0]["text"]);
            Assert.Equal(new string('z', 20000), (string)blocks[2]["content"]);
        }
    }
}
