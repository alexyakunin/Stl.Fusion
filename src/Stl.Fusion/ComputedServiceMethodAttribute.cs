using System;

namespace Stl.Fusion
{
    [AttributeUsage(AttributeTargets.Method)]
    public class ComputedServiceMethodAttribute : InterceptedMethodAttribute
    {
        // In seconds, NaN means "use default"
        public double KeepAliveTime { get; set; } = Double.NaN;
        public double ErrorAutoInvalidateTimeout { get; set; } = Double.NaN;
        public double AutoInvalidateTimeout { get; set; } = Double.NaN;

        public ComputedServiceMethodAttribute() { }
        public ComputedServiceMethodAttribute(bool isEnabled) : base(isEnabled) { }
    }
}
