namespace Ago.Chat.Domain;

/// <summary>
/// Where the widget's launcher renders on a visitor's page - one of the two fixed, validated fields
/// `11-01`'s own ADR (`adr/0028`) chose over an open-ended styling API. <see cref="BottomRight"/> is
/// the first member (so it is also the CLR default, `default(Position)`) because it is the launcher
/// position every real chat widget defaults to - a freshly self-registered or pre-existing
/// <see cref="Site"/> that never called <see cref="Site.UpdateWidgetConfig"/> should render exactly
/// where an operator would expect, never at position `0` by accident of enum ordering.
/// </summary>
public enum Position
{
    BottomRight,
    BottomLeft,
}
