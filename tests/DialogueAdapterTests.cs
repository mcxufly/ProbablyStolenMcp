using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using ProbablyStolenMcp;

static class DialogueTests
{
    static int checks;
    static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    static void Main()
    {
        var values = new[] { "old", "middle", "new" };
        var page = DialogueAdapter.BuildHistoryPage(values, "snap", "session", 0, 2);
        Check((int)page["total"] == 3, "bounded native total");
        Check((int)page["entries"][0]["record_index"] == 0, "native order");
        Check((string)page["entries"][1]["raw_text"] == "middle", "pagination");
        Check((int)page["next_offset"] == 2, "next offset");
        var separator = DialogueAdapter.BuildHistoryPage(new[] { "---" }, "s", "x", 0, 1)["entries"][0];
        Check((bool)separator["separator"], "separator preserved");
        string emoji = char.ConvertFromUtf32(0x1F600);
        string longText = string.Concat(Enumerable.Repeat(emoji, 4097));
        var clipped = DialogueAdapter.BuildHistoryPage(new[] { longText }, "s", "x", 0, 1)["entries"][0];
        Check((bool)clipped["text_truncated"], "scalar truncation");
        Check((string)clipped["raw_text"] == string.Concat(Enumerable.Repeat(emoji, 4096)), "surrogate-safe scalar limit");
        Check(DialogueAdapter.CleanText("a\ud800b", 10) == "a�b", "invalid surrogate replacement");
        Console.WriteLine($"PASS: {checks} native dialogue history assertions");
    }
}
