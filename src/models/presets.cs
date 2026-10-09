namespace PresetTrader;

public class MatchedSet
{
    public string? RootTpl { get; set; }
    public List<AttachmentRef>? Attachments { get; set; }
}

public class AttachmentRef
{
    public string? Slot { get; set; }
    public string? Tpl { get; set; }
}
