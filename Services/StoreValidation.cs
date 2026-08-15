namespace WFly.Services;

internal static class StoreValidation
{
    public static string NewId() => Guid.NewGuid().ToString("N");

    public static string RequiredText(string? value, string fieldName, int maximumLength)
    {
        var normalized = OptionalText(value, maximumLength);
        if (normalized is null)
        {
            throw new ArgumentException($"{fieldName} is required.", fieldName);
        }

        return normalized;
    }

    public static string? OptionalText(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.Length > maximumLength || normalized.IndexOf('\0') >= 0)
        {
            throw new ArgumentException($"Text must be at most {maximumLength} characters and cannot contain NUL.");
        }

        return normalized;
    }
}
