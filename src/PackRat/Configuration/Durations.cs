using System.Globalization;

namespace PackRat.Configuration;

/// <summary>
///   Parses durations such as <c>500ms</c>, <c>30s</c>, <c>5m</c>,
///   <c>1h30m</c>, <c>2d</c> or <c>00:01:30</c>. A bare number is rejected
///   because <see cref="TimeSpan.Parse(string)" /> would read <c>30</c> as
///   thirty days.
/// </summary>
public static class Durations {
  public static bool TryParse(string? text, out TimeSpan duration) {
    duration = TimeSpan.Zero;
    string value = text?.Trim().ToLowerInvariant() ?? string.Empty;
    if (value.Length == 0) {
      return false;
    }

    if (value.Contains(':')) {
      return TimeSpan.TryParse(
        value,
        CultureInfo.InvariantCulture,
        out duration
      ) && duration >= TimeSpan.Zero;
    }

    TimeSpan total = TimeSpan.Zero;
    int position = 0;
    while (position < value.Length) {
      int number_start = position;
      while (position < value.Length &&
             (char.IsAsciiDigit(value[position]) || value[position] == '.')) {
        position++;
      }

      int unit_start = position;
      while (position < value.Length && char.IsAsciiLetter(value[position])) {
        position++;
      }

      string number = value[number_start..unit_start];
      string unit = value[unit_start..position];
      if (!double.TryParse(
            number,
            NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture,
            out double amount
          )) {
        return false;
      }

      TimeSpan? part = unit switch {
        "ms" => TimeSpan.FromMilliseconds(amount),
        "s" => TimeSpan.FromSeconds(amount),
        "m" => TimeSpan.FromMinutes(amount),
        "h" => TimeSpan.FromHours(amount),
        "d" => TimeSpan.FromDays(amount),
        _ => null
      };
      if (part is null) {
        return false;
      }

      total += part.Value;
    }

    duration = total;
    return true;
  }

  public static string Format(TimeSpan duration) {
    if (duration.TotalSeconds < 1) {
      return $"{duration.TotalMilliseconds:0}ms";
    }

    if (duration.TotalMinutes < 1) {
      return $"{duration.TotalSeconds:0.#}s";
    }

    if (duration.TotalHours < 1) {
      return $"{duration.TotalMinutes:0.#}m";
    }

    return $"{duration.TotalHours:0.#}h";
  }
}
