using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Stl.Fusion.Tests.Services;
using Xunit;
using Xunit.Abstractions;

namespace Stl.Fusion.Tests
{
    public class TryGetExistingTest : SimpleFusionTestBase
    {
        public TryGetExistingTest(ITestOutputHelper @out) : base(@out) { }

        protected override void ConfigureCommonServices(ServiceCollection services)
            => services.AddFusion().AddAuthentication();

        [Fact]
        public async Task BasicTest()
        {
            var services = CreateServiceProviderFor<CounterService>();
            var counters = services.GetRequiredService<CounterService>();

            var c = Computed.TryGetExisting(() => counters.Get("a"));
            c.Should().BeNull();

            c = await Computed.Capture(_ => counters.Get("a"));
            c.Value.Should().Be(0);
            var c1 = Computed.TryGetExisting(() => counters.Get("a"));
            c1.Should().BeSameAs(c);

            await counters.Increment("a");
            c.IsConsistent().Should().BeFalse();
            c1 = Computed.TryGetExisting(() => counters.Get("a"));
            c1.Should().BeNull();
        }
    }
}
