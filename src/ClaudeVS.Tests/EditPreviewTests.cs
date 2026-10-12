using System;
using ClaudeVS.Chat;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ClaudeVS.Tests
{
    public class EditPreviewTests
    {
        [Fact]
        public void Write_renvoie_le_contenu_propose() => Assert.Equal("neuf", EditPreview.Apply("Write", JObject.Parse("{content:'neuf'}"), "ancien"));

        [Fact]
        public void Write_sans_contenu_renvoie_vide() => Assert.Equal("", EditPreview.Apply("Write", new JObject(), "ancien"));

        [Fact]
        public void Edit_simple_remplace_la_premiere_occurrence()
            => Assert.Equal("X b a", EditPreview.Apply("Edit", JObject.Parse("{old_string:'a',new_string:'X'}"), "a b a"));

        [Fact]
        public void Edit_replace_all_remplace_toutes_les_occurrences()
            => Assert.Equal("X b X", EditPreview.Apply("Edit", JObject.Parse("{old_string:'a',new_string:'X',replace_all:true}"), "a b a"));

        [Fact]
        public void Edit_old_string_introuvable_leve_une_exception()
            => Assert.Throws<InvalidOperationException>(() => EditPreview.Apply("Edit", JObject.Parse("{old_string:'zzz',new_string:'X'}"), "a b a"));

        [Fact]
        public void Edit_fichier_CRLF_avec_old_string_LF()
            => Assert.Equal("x\r\ny\r\nc", EditPreview.Apply("Edit", JObject.Parse("{old_string:'a\\nb',new_string:'x\\ny'}"), "a\r\nb\r\nc"));

        [Fact]
        public void Edit_fichier_LF_avec_old_string_CRLF_introuvable()
            => Assert.Throws<InvalidOperationException>(() => EditPreview.Apply("Edit", JObject.Parse("{old_string:'a\\r\\nb',new_string:'x'}"), "a\nb"));

        [Fact]
        public void Edit_old_string_vide_donne_new_string_comme_contenu()
            => Assert.Equal("cree", EditPreview.Apply("Edit", JObject.Parse("{old_string:'',new_string:'cree'}"), "ancien"));

        [Fact]
        public void MultiEdit_enchaine_les_remplacements()
            => Assert.Equal("c b", EditPreview.Apply("MultiEdit",
                JObject.Parse("{edits:[{old_string:'a',new_string:'b'},{old_string:'b b',new_string:'c b'}]}"), "a b"));

        [Fact]
        public void MultiEdit_sans_edits_renvoie_l_original()
            => Assert.Equal("a", EditPreview.Apply("MultiEdit", new JObject(), "a"));

        [Fact]
        public void Outil_inconnu_leve_NotSupported()
            => Assert.Throws<NotSupportedException>(() => EditPreview.Apply("Read", new JObject(), "a"));

        [Fact]
        public void Replace_new_string_null_supprime_old_string()
            => Assert.Equal("a", EditPreview.Replace("abc", "bc", null, false));

        [Fact]
        public void BuildUpdatedInput_sans_retouche_renvoie_une_copie_identique()
        {
            var input = JObject.Parse("{file_path:'f',old_string:'a',new_string:'b'}");
            var res = EditPreview.BuildUpdatedInput("Edit", input, "f", "a", "b", null);
            Assert.True(JToken.DeepEquals(input, res));
            Assert.NotSame(input, res);
        }

        [Fact]
        public void BuildUpdatedInput_edite_identique_au_propose_est_sans_retouche()
        {
            var input = JObject.Parse("{file_path:'f',old_string:'a',new_string:'b'}");
            Assert.True(JToken.DeepEquals(input, EditPreview.BuildUpdatedInput("Edit", input, "f", "a", "b", "b")));
        }

        [Fact]
        public void BuildUpdatedInput_Write_retouche_remplace_le_contenu()
        {
            var input = JObject.Parse("{file_path:'f',content:'propose'}");
            var res = EditPreview.BuildUpdatedInput("Write", input, "f", "", "propose", "retouche");
            Assert.Equal("retouche", (string)res["content"]);
            Assert.Equal("f", (string)res["file_path"]);
            Assert.Equal("propose", (string)input["content"]); // l'entrée d'origine n'est pas modifiée
        }

        [Fact]
        public void BuildUpdatedInput_Edit_retouche_fichier_non_vide()
        {
            var input = JObject.Parse("{file_path:'f',old_string:'a',new_string:'b'}");
            var res = EditPreview.BuildUpdatedInput("Edit", input, "C:\\f", "contenu", "propose", "retouche");
            Assert.Equal("C:\\f", (string)res["file_path"]);
            Assert.Equal("contenu", (string)res["old_string"]);
            Assert.Equal("retouche", (string)res["new_string"]);
            Assert.False((bool)res["replace_all"]);
        }

        [Fact]
        public void BuildUpdatedInput_Edit_retouche_fichier_vide_donne_old_string_vide()
        {
            var input = JObject.Parse("{file_path:'f',old_string:'',new_string:'b'}");
            var res = EditPreview.BuildUpdatedInput("Edit", input, "f", "", "b", "retouche");
            Assert.Equal("", (string)res["old_string"]);
            Assert.Equal("retouche", (string)res["new_string"]);
        }

        [Fact]
        public void BuildUpdatedInput_MultiEdit_retouche_devient_une_seule_substitution()
        {
            var input = JObject.Parse("{file_path:'f',edits:[{old_string:'a',new_string:'b'},{old_string:'c',new_string:'d'}]}");
            var res = EditPreview.BuildUpdatedInput("MultiEdit", input, "f", "orig", "prop", "ret");
            var edits = (JArray)res["edits"];
            Assert.Single(edits);
            Assert.Equal("orig", (string)edits[0]["old_string"]);
            Assert.Equal("ret", (string)edits[0]["new_string"]);
        }
    }
}
