using System;

namespace Execor.Inference.Services;

public class InferenceMetricsService
{
    private DateTime _startTime;
    private int _tokenCount;

    public void Start()
    {
        _startTime = DateTime.UtcNow;
        _tokenCount = 0;
    }

    public void AddToken(string token)
    {
        _tokenCount++;
    }

    public float GetTokensPerSecond()
    {
        // Fix: Return 0 if the timer hasn't started
        if (_startTime == default) return 0;

        var elapsed = (float)(DateTime.UtcNow - _startTime).TotalSeconds;
        if (elapsed <= 0) return 0;
        return _tokenCount / elapsed;
    }

    public float GetElapsedSeconds()
    {
        // Fix: Prevents calculating time since the year 0001
        if (_startTime == default) return 0;

        return (float)(DateTime.UtcNow - _startTime).TotalSeconds;
    }
}