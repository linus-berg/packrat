using System.Globalization;

namespace PackRat.Sync;

public static class Sizes {
  private static readonly string[] S_UNITS_ = [
    "B",
    "KiB",
    "MiB",
    "GiB",
    "TiB"
  ];

  public static string Format(long bytes) {
    double value = bytes;
    int unit = 0;
    while (value >= 1024 && unit < S_UNITS_.Length - 1) {
      value /= 1024;
      unit++;
    }

    string format = unit == 0 ? "0" : "0.#";
    return value.ToString(format, CultureInfo.InvariantCulture) + " " +
           S_UNITS_[unit];
  }
}
