using HwFanControl.Core.Curves;

namespace HwFanControl.Core.Safety;

/// <summary>
/// Per-channel PWM post-processing: minimum floor, kickstart from standstill, rate limiting.
/// Pure managed — no hardware dependency.
/// </summary>
public sealed class PwmSafetyFilter
{
    private readonly ChannelRuntimeState[] _states = new ChannelRuntimeState[FanLogicLimits.MaxFanChannels];

    public PwmSafetyFilter()
    {
        for (int i = 0; i < _states.Length; i++)
            _states[i] = new ChannelRuntimeState();
    }

    public float Apply(
        int channel,
        float requestedPwm,
        ChannelSafetyOptions options,
        DateTimeOffset nowUtc,
        float deltaSeconds)
    {
        if ((uint)channel >= (uint)_states.Length)
            return System.Math.Clamp(requestedPwm, 0f, 100f);

        options ??= new ChannelSafetyOptions();
        var state = _states[channel];
        float previous = state.LastOutputPwm;
        float pwm = System.Math.Clamp(requestedPwm, 0f, 100f);

        // Hard floor: 0% allowed (off); otherwise enforce minimum.
        if (pwm > 0f && pwm < options.MinimumPwmPercent)
            pwm = options.MinimumPwmPercent;

        // Kickstart when rising from standstill.
        bool wasStopped = previous <= 0.5f;
        bool wantsSpin = pwm > 0.5f;
        if (wasStopped && wantsSpin && options.KickstartDurationSeconds > 0f && options.KickstartPwmPercent > 0f)
        {
            state.KickstartUntilUtc = nowUtc.AddSeconds(options.KickstartDurationSeconds);
        }

        if (options.KickstartDurationSeconds > 0f && nowUtc < state.KickstartUntilUtc && wantsSpin)
        {
            pwm = System.Math.Max(pwm, options.KickstartPwmPercent);
        }

        // Rate limit relative to last output.
        float dt = System.Math.Max(0.001f, deltaSeconds);
        float maxUp = options.RateLimitUpPercentPerSecond * dt;
        float maxDown = options.RateLimitDownPercentPerSecond * dt;
        float delta = pwm - previous;
        if (delta > maxUp)
            pwm = previous + maxUp;
        else if (delta < -maxDown)
            pwm = previous - maxDown;

        pwm = System.Math.Clamp(pwm, 0f, 100f);
        state.LastOutputPwm = pwm;
        return pwm;
    }

    public void ResetChannel(int channel)
    {
        if ((uint)channel >= (uint)_states.Length)
            return;
        _states[channel] = new ChannelRuntimeState();
    }

    public float GetLastOutput(int channel) =>
        (uint)channel < (uint)_states.Length ? _states[channel].LastOutputPwm : 0f;

    private sealed class ChannelRuntimeState
    {
        public float LastOutputPwm;
        public DateTimeOffset KickstartUntilUtc;
    }
}
