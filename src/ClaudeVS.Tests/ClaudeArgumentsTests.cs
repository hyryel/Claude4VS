using System.Linq;
using ClaudeVS.Chat;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ClaudeVS.Tests
{
    public class ClaudeSettingsTests
    {
        [Theory]
        [InlineData("default")] [InlineData("acceptEdits")] [InlineData("plan")] [InlineData("bypassPermissions")] [InlineData("dontAsk")]
        public void Modes_valides(string m) => Assert.True(ClaudeSettings.IsValidMode(m));

        [Theory]
        [InlineData("")] [InlineData("Plan")] [InlineData("--foo")] [InlineData("plan --foo")] [InlineData(null)]
        public void Modes_invalides(string m) => Assert.False(ClaudeSettings.IsValidMode(m));

        [Theory]
        [InlineData("low")] [InlineData("medium")] [InlineData("high")] [InlineData("xhigh")] [InlineData("max")]
        public void Efforts_valides(string e) => Assert.True(ClaudeSettings.IsValidEffort(e));

        [Theory]
        [InlineData("")] [InlineData("HIGH")] [InlineData("--foo")] [InlineData("high ")] [InlineData(null)]
        public void Efforts_invalides(string e) => Assert.False(ClaudeSettings.IsValidEffort(e));

        [Theory]
        [InlineData("opus")] [InlineData("claude-sonnet-4-5")] [InlineData("claude-opus-4-1-20250805")]
        [InlineData("sonnet[1m]")] [InlineData("anthropic.claude-v2:1")] [InlineData("us.anthropic/model_x")]
        public void Modeles_valides(string m) => Assert.True(ClaudeSettings.IsValidModel(m));

        [Theory]
        [InlineData("")] [InlineData("--foo")] [InlineData("-m")] [InlineData("a b")] [InlineData(" opus")]
        [InlineData("opus --dangerously-skip-permissions")] [InlineData("a\"b")] [InlineData("a;b")] [InlineData("a&b")]
        public void Modeles_invalides(string m) => Assert.False(ClaudeSettings.IsValidModel(m));

        [Fact]
        public void Modele_de_plus_de_200_caracteres_invalide()
        {
            Assert.True(ClaudeSettings.IsValidModel(new string('a', 200)));
            Assert.False(ClaudeSettings.IsValidModel(new string('a', 201)));
        }
    }

    public class ClaudeArgumentsTests
    {
        private static readonly string[] Prefix = { "/c", "claude" };

        [Fact]
        public void Build_conserve_les_arguments_de_prefixe_en_tete_et_l_ordre_de_base()
        {
            var a = ClaudeArguments.Build(Prefix, new ClaudeOptions { PermissionMode = "plan" });
            Assert.Equal(new[]
            {
                "/c", "claude", "--output-format", "stream-json", "--verbose", "--input-format", "stream-json",
                "--include-partial-messages", "--permission-prompt-tool", "stdio", "--permission-mode", "plan",
                "--replay-user-messages",
            }, a.ToArray());
        }

        [Fact]
        public void Build_sans_options_facultatives_omet_model_effort_resume_mcp()
        {
            var a = ClaudeArguments.Build(new string[0], new ClaudeOptions { PermissionMode = "default" });
            Assert.DoesNotContain("--model", a);
            Assert.DoesNotContain("--effort", a);
            Assert.DoesNotContain("--resume", a);
            Assert.DoesNotContain("--mcp-config", a);
            Assert.DoesNotContain("--allowedTools", a);
            Assert.DoesNotContain("--append-system-prompt", a);
        }

        [Fact]
        public void Build_ajoute_model_effort_resume_avec_leur_valeur()
        {
            var a = ClaudeArguments.Build(new string[0], new ClaudeOptions { PermissionMode = "default", Model = "opus", Effort = "high", ResumeSessionId = "abc" });
            Assert.Equal("opus", a[a.IndexOf("--model") + 1]);
            Assert.Equal("high", a[a.IndexOf("--effort") + 1]);
            Assert.Equal("abc", a[a.IndexOf("--resume") + 1]);
            Assert.True(a.IndexOf("--model") < a.IndexOf("--effort") && a.IndexOf("--effort") < a.IndexOf("--resume"));
            Assert.True(a.IndexOf("--resume") < a.IndexOf("--replay-user-messages"));
        }

        [Fact]
        public void Build_avec_mcp_ajoute_config_consigne_et_outils_autorises()
        {
            var a = ClaudeArguments.Build(new string[0], new ClaudeOptions { PermissionMode = "default", McpConfigPath = "C:\\t\\mcp.json" });
            Assert.Equal("C:\\t\\mcp.json", a[a.IndexOf("--mcp-config") + 1]);
            Assert.Contains("renameSymbol", a[a.IndexOf("--append-system-prompt") + 1]);
            var allowed = a[a.IndexOf("--allowedTools") + 1].Split(',');
            Assert.Contains("mcp__visualstudio__findSymbol", allowed);
            Assert.All(allowed, t => Assert.StartsWith("mcp__visualstudio__", t));
            Assert.True(a.IndexOf("--replay-user-messages") < a.IndexOf("--mcp-config"));
        }

        [Fact]
        public void Build_les_outils_qui_agissent_sur_le_programme_ne_sont_pas_pre_autorises()
        {
            var a = ClaudeArguments.Build(new string[0], new ClaudeOptions { PermissionMode = "default", McpConfigPath = "m.json" });
            var allowed = a[a.IndexOf("--allowedTools") + 1];
            foreach (var t in new[] { "debugStart", "debugContinue", "debugStop", "debugEvaluate", "renameSymbol" })
                Assert.DoesNotContain("mcp__visualstudio__" + t, allowed.Split(','));
        }

        [Fact]
        public void Build_ne_modifie_pas_la_liste_de_prefixe()
        {
            var prefix = new System.Collections.Generic.List<string> { "x" };
            ClaudeArguments.Build(prefix, new ClaudeOptions { PermissionMode = "plan" });
            Assert.Single(prefix);
        }

        [Fact]
        public void McpConfig_structure_du_serveur_visualstudio()
        {
            var cfg = ClaudeArguments.McpConfig(12345, "tok");
            var s = cfg["mcpServers"]["visualstudio"];
            Assert.Equal("ws", (string)s["type"]);
            Assert.Equal("ws://127.0.0.1:12345/tools", (string)s["url"]);
            Assert.Equal("tok", (string)s["headers"]["x-claude-code-ide-authorization"]);
            Assert.True((bool)s["alwaysLoad"]);
            Assert.Single(((JObject)cfg["mcpServers"]).Properties());
        }
    }
}
