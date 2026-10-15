using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SententiamConverte.Services;

/// <summary>Wortweiser Vergleich zweier Fassungen: [−gelöscht−] {+eingefügt+}.</summary>
public static class TextDiff
{
    public static string Words(string before, string after)
    {
        var a = before.Split(' ');
        var b = after.Split(' ');
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : System.Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        var parts = new List<string>();
        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (a[x] == b[y]) { parts.Add(a[x]); x++; y++; }
            else if (lcs[x + 1, y] >= lcs[x, y + 1]) parts.Add("[−" + a[x++] + "−]");
            else parts.Add("{+" + b[y++] + "+}");
        }
        while (x < a.Length) parts.Add("[−" + a[x++] + "−]");
        while (y < b.Length) parts.Add("{+" + b[y++] + "+}");
        return string.Join(" ", parts);
    }
}
