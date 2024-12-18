public class RateLimitingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IOptions<RateLimitingOptions> _options;

    // Store request timestamps per IP
    private static readonly ConcurrentDictionary<string, ConcurrentQueue<DateTime>> _requestLog
        = new ConcurrentDictionary<string, ConcurrentQueue<DateTime>>();

    // Track consecutive limit hits per IP
    private static readonly ConcurrentDictionary<string, int> _consecutiveLimitHits
        = new ConcurrentDictionary<string, int>();

    // Blocklist: IP -> Unblock Time
    private static readonly ConcurrentDictionary<string, DateTime> _blockList
        = new ConcurrentDictionary<string, DateTime>();

    public RateLimitingMiddleware(RequestDelegate next, IOptions<RateLimitingOptions> options)
    {
        _next = next;
        _options = options;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        string clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        // Check if IP is currently blocked
        if (_options.Value.EnableBlocking && _blockList.TryGetValue(clientIp, out var blockedUntil))
        {
            if (DateTime.UtcNow < blockedUntil)
            {
                // Calculate remaining block time
                var remaining = (int)(blockedUntil - DateTime.UtcNow).TotalSeconds;
                // Convert to a human-readable format (e.g., seconds or minutes)
                var waitMinutes = (int)Math.Ceiling(remaining / 60.0);

                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await context.Response.WriteAsync($"You are temporarily blocked due to repeated rate limit violations. Try again in about {waitMinutes} minutes.");
                return;
            }
            else
            {
                // Unblock IP after block time has passed
                _blockList.TryRemove(clientIp, out _);
                _consecutiveLimitHits[clientIp] = 0;
            }
        }

        var timestamps = _requestLog.GetOrAdd(clientIp, _ => new ConcurrentQueue<DateTime>());

        var now = DateTime.UtcNow;
        timestamps.Enqueue(now);

        // Remove old timestamps outside the window
        var window = TimeSpan.FromSeconds(_options.Value.WindowInSeconds);
        while (timestamps.TryPeek(out var oldest) && (now - oldest) > window)
        {
            timestamps.TryDequeue(out _);
        }

        // Check if limit is exceeded
        if (timestamps.Count > _options.Value.RequestsPerWindow)
        {
            // If blocking is enabled, check how many times the IP has hit the limit consecutively
            int hits = 0;
            if (_options.Value.EnableBlocking)
            {
                hits = _consecutiveLimitHits.AddOrUpdate(clientIp, 1, (_, current) => current + 1);
            }

            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;

            // If blocking is enabled and we are at the threshold, block the IP
            if (_options.Value.EnableBlocking && hits >= _options.Value.ConsecutiveLimitForBlock)
            {
                var blockUntil = DateTime.UtcNow.AddMinutes(_options.Value.BlockDurationInMinutes);
                _blockList[clientIp] = blockUntil;

                var waitMinutes = _options.Value.BlockDurationInMinutes;
                await context.Response.WriteAsync($"Too Many Requests. You have been blocked due to repeated violations. Try again after about {waitMinutes} minutes.");
            }
            else
            {
                var waitSeconds = _options.Value.WindowInSeconds;
                // Not blocked, just rate-limited for now.
                await context.Response.WriteAsync($"Too Many Requests. Please try after {waitSeconds} seconds");
            }

            return;
        }
        else
        {
            // Limit not reached, reset consecutive hits if previously set
            if (_consecutiveLimitHits.ContainsKey(clientIp))
            {
                _consecutiveLimitHits[clientIp] = 0;
            }
        }

        await _next(context);
    }
}