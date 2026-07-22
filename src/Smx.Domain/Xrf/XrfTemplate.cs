namespace Smx.Domain.Xrf;

/// The column contract, defined once.
///
/// The parser and the downloadable template both read THIS — because a template that produces a file
/// the parser rejects is worse than shipping no template at all: it teaches the physicist a format
/// that does not work, and the operator gets the blame.
public static class XrfTemplate
{
    public const string Component = "component";
    public const string Element = "element";
    public const string Line = "line";
    public const string Status = "status";
    public const string SignalNote = "signal_note";
    public const string BackgroundLevel = "background_level";
    public const string BackgroundUnit = "background_unit";
    public const string DeviceModel = "device_model";
    public const string DeviceLod = "device_lod";
    public const string DeviceLodUnit = "device_lod_unit";

    public static readonly IReadOnlyList<string> Columns =
    [
        Component, Element, Line, Status, SignalNote,
        BackgroundLevel, BackgroundUnit, DeviceModel, DeviceLod, DeviceLodUnit,
    ];

    /// The unit every number must be in. Not a default and not a fallback — a value in any other unit
    /// is refused, because converting it needs a calibration this code does not have.
    public const string Ppm = "ppm";

    /// A template with example rows, one of each kind the operator will get wrong: a conditional row
    /// (which needs the signal note) and an X row (measured, rejected, still recorded).
    ///
    /// No example field contains a comma — Template_ParsesAsItsOwnValidInput splits naively on
    /// purpose, so the template stays something a physicist can also read at a glance.
    public static string Csv =>
        string.Join(",", Columns) + "\n" +
        "bottle,Ba,Ka,V,,12.5,ppm,Niton XL5,3.0,ppm\n" +
        "bottle,Sr,Ka,L,shoulder on the Ba Ka line - resolved at 40 kV,8.1,ppm,Niton XL5,2.5,ppm\n" +
        "bottle,Fe,Ka,X,,940.0,ppm,Niton XL5,1.8,ppm\n";
}
