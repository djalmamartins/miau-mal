namespace Miau.Desktop;

public sealed record AcceptanceCheck(bool Passed, IReadOnlyList<string> Missing)
{
    public string Summary => Passed ? "Critérios de aceitação atendidos." : "Pendências: " + string.Join(", ", Missing);
}

public static class VisualAcceptance
{
    public static AcceptanceCheck Evaluate(string task, string html, string css)
    {
        var t = task.ToLowerInvariant();
        var h = html.ToLowerInvariant();
        var c = css.ToLowerInvariant();
        var missing = new List<string>();

        Require(t, h, missing, new[] { "header" }, "<header", "header");
        Require(t, h, missing, new[] { "hero" }, new[] { "class=\"hero", "class='hero", "id=\"hero", "id='hero" }, "hero");
        Require(t, h, missing, new[] { "produto", "products" }, new[] { "product", "produto" }, "produtos em destaque");
        Require(t, h, missing, new[] { "editorial" }, new[] { "editorial", "story", "historia", "história" }, "seção editorial");
        Require(t, h, missing, new[] { "chamada", "cta", "call to action" }, new[] { "class=\"cta", "class='cta", "<button", "call-to-action" }, "chamadas/CTA");
        Require(t, h, missing, new[] { "footer", "rodapé", "rodape" }, "<footer", "footer");

        if (t.Contains("responsiv"))
        {
            if (!h.Contains("name=\"viewport\"") && !h.Contains("name='viewport'")) missing.Add("viewport meta");
            if (!(c.Contains("@media") || c.Contains("@container"))) missing.Add("responsividade CSS (@media/@container)");
        }

        return new(missing.Count == 0, missing);
    }

    static void Require(string task, string source, List<string> missing, string[] requestedTerms, string evidence, string label)
    {
        if (requestedTerms.Any(task.Contains) && !source.Contains(evidence, StringComparison.OrdinalIgnoreCase))
            missing.Add(label);
    }

    static void Require(string task, string source, List<string> missing, string[] requestedTerms, string[] evidence, string label)
    {
        if (requestedTerms.Any(task.Contains) && !evidence.Any(source.Contains))
            missing.Add(label);
    }
}
