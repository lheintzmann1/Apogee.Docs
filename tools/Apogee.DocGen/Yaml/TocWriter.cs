namespace Apogee.DocGen.Yaml;

public sealed class TocNode
{
    public required string Name { get; init; }
    public string? Uid { get; init; }
    public string? Href { get; init; }
    public List<TocNode> Items { get; } = [];
}

public static class TocWriter
{
    public static string Write(IReadOnlyList<TocNode> roots)
    {
        var w = new YamlWriter();
        WriteNodes(w, roots);
        return w.ToString();
    }

    private static void WriteNodes(YamlWriter w, IReadOnlyList<TocNode> nodes)
    {
        foreach (var node in nodes)
        {
            using (w.Item())
            {
                w.Key("name", node.Name);
                w.Key("uid", node.Uid);
                w.Key("href", node.Href);
                if (node.Items.Count > 0)
                {
                    using (w.Section("items"))
                        WriteNodes(w, node.Items);
                }
            }
        }
    }
}
