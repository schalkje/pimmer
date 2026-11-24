namespace Pimmer.App.Components;

public enum DialogMode { Activate, Extend }

public sealed class DialogModel
{
    public DialogMode Mode { get; set; }
    public string Title { get; set; } = "";
    public List<Pimmer.Core.PimItem> Items { get; set; } = [];
    public int DurationMinutes { get; set; } = 60;
    public string Justification { get; set; } = "";
    public string TicketNumber { get; set; } = "";
    public string TicketSystem { get; set; } = "";
    public string? GroupId { get; set; }
    public bool SetAsDefault { get; set; }
    public bool Loading { get; set; }
}
