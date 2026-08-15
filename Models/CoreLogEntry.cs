namespace WFly.Models;

internal sealed record CoreLogEntry(DateTimeOffset Timestamp, string Stream, string Message);
