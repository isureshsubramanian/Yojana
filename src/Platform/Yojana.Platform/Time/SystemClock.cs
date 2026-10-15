using Yojana.Platform.Contracts.Time;

namespace Yojana.Platform.Time;

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
