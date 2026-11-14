using System.Text.Json.Nodes;

namespace SpatialAudioServer.Rooms;

/// <summary>Collects field errors and throws them together as a <see cref="DomainValidationException"/>.</summary>
internal sealed class Validation
{
    public const int MaxNameLength = 200;
    public const int MaxExternalIdLength = 200;
    public const int MaxMetadataBytes = 16 * 1024;
    public const double MaxGain = 4.0; // audioamplify headroom; 1.0 = unity

    private readonly Dictionary<string, string[]> _errors = [];

    public Validation Required(string field, string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Add(field, "is required");
        }
        else if (value.Trim().Length > maxLength)
        {
            Add(field, $"must be at most {maxLength} characters");
        }
        return this;
    }

    public Validation Optional(string field, string? value, int maxLength)
    {
        if (value is not null && (value.Length > maxLength || string.IsNullOrWhiteSpace(value)))
        {
            Add(field, $"must be 1 to {maxLength} non-blank characters when given");
        }
        return this;
    }

    public Validation Range(string field, double? value, double min, double max)
    {
        if (value is { } v && (!double.IsFinite(v) || v < min || v > max))
        {
            Add(field, $"must be between {min} and {max}");
        }
        return this;
    }

    public Validation OneOf(string field, string? value, Func<string, bool> exists, IEnumerable<string> allowed)
    {
        if (value is not null && !exists(value))
        {
            Add(field, $"must be one of: {string.Join(", ", allowed)}");
        }
        return this;
    }

    /// <summary>A rule that the other helpers do not cover: fails <paramref name="field"/> unless <paramref name="ok"/>.</summary>
    public Validation Check(string field, bool ok, string message)
    {
        if (!ok)
        {
            Add(field, message);
        }
        return this;
    }

    public Validation Position(string field, Vec3? value)
    {
        if (value is not null && !(double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z)))
        {
            Add(field, "must have finite x, y and z");
        }
        return this;
    }

    public Validation Orientation(string field, Quat? value)
    {
        if (value is null)
        {
            return this;
        }
        var finite = double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z) && double.IsFinite(value.W);
        var length = Math.Sqrt(value.X * value.X + value.Y * value.Y + value.Z * value.Z + value.W * value.W);
        if (!finite || length < 1e-6)
        {
            Add(field, "must be a non-zero quaternion with finite x, y, z and w");
        }
        return this;
    }

    public Validation Metadata(string field, JsonObject? value)
    {
        if (value is not null && System.Text.Encoding.UTF8.GetByteCount(value.ToJsonString()) > MaxMetadataBytes)
        {
            Add(field, $"must be at most {MaxMetadataBytes} bytes of JSON");
        }
        return this;
    }

    public void ThrowIfInvalid()
    {
        if (_errors.Count > 0)
        {
            throw new DomainValidationException(_errors);
        }
    }

    private void Add(string field, string message) =>
        _errors[field] = _errors.TryGetValue(field, out var existing) ? [.. existing, message] : [message];
}
