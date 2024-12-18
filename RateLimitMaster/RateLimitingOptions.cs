public class RateLimitingOptions
{
    public int RequestsPerWindow { get; set; } = 6;
    public int WindowInSeconds { get; set; } = 5;
    public int ConsecutiveLimitForBlock { get; set; } = 3;
    public int BlockDurationInMinutes { get; set; } = 15;
    public bool EnableBlocking { get; set; } = false;
}