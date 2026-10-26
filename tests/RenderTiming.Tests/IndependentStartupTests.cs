using Xunit;

namespace Fahrenheit.Mods.Vrr;

[Collection("Render scheduler")]
public sealed class IndependentStartupTests
{
    [Fact]
    public void Requested_independent_mode_cannot_install_scheduler_without_qualification()
    {
        NativeClock.Hooks.Clear();
        var module = new VrrModule();
        Assert.True(module.Preflight());
        Assert.True(module.Requested);
        Assert.False(module.Effective);
        Assert.Equal(1u, module.Limiter);
        Assert.True(module.InitScheduler());
        Assert.Empty(NativeClock.Hooks);
        Assert.Equal(0, module.FieldPauseInstalls);
        Assert.True(module.InitPresenter());
        Assert.Equal(1, module.SharedTextureObservers);
        Assert.Equal(0, module.IndependentSwapchainHooks);
        long serial = RenderPassContext.SimulationSerial;
        Assert.False(module.RunNativePass());
        Assert.True(module.RunNativePass());
        Assert.Equal(serial + 2, RenderPassContext.SimulationSerial);
    }

    [Fact]
    public void Vetoed_explicit_120_request_is_refused_before_installation()
    {
        NativeClock.Hooks.Clear();
        var module = new VrrModule { ChosenRate = 120 };
        Assert.False(module.Preflight());
        Assert.True(module.Requested);
        Assert.False(module.Effective);
        Assert.Empty(NativeClock.Hooks);
    }

    [Fact]
    public void Disabled_request_keeps_native_mode_without_veto_warning()
    {
        var module = new VrrModule();
        module.RequestIndependent(false);
        Assert.True(module.Preflight());
        Assert.False(module.Requested);
        Assert.False(module.Effective);
        Assert.Equal(0, module.WarningCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("{\"can_activate\":true}")]
    public void Missing_malformed_or_positive_claims_cannot_authorize(string? json)
    {
        using var stream = json is null ? null : new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        Assert.False(IndependentPresentationQualification.Evaluate(stream).Enabled);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(14)]
    [InlineData(15)]
    public void Complete_veto_snapshot_never_authorizes_any_contract(int contractCount)
    {
        var snapshot = new
        {
            schema_version = "1.0", policy = "independent_presentation_veto_only", supported_build = "Steam 25501027",
            canonical_contract_snapshot_sha256 = new string('a', 64),
            required_contracts = Enumerable.Range(0, contractCount).Select(n => $"contract-{n}").ToArray(),
            blockers = Enumerable.Range(0, 73).Select(n => $"blocker-{n}").ToArray(),
            source_inputs = new[] { new { path = "src/VrrModule.cs", sha256 = new string('b', 64) } },
            generator_inputs = new[] { new { path = "src/Generator.cs", sha256 = new string('c', 64) } }
        };
        string json = JsonSerializer.Serialize(snapshot);
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        var decision = IndependentPresentationQualification.Evaluate(stream);
        Assert.False(decision.Enabled);
        Assert.Equal(contractCount, decision.ContractCount);
        Assert.Equal(73, decision.BlockerCount);
        using var unknown = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json.Replace("veto_only", "approved")));
        Assert.Equal(0, IndependentPresentationQualification.Evaluate(unknown).ContractCount);
        using var positive = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json[..^1] + ",\"can_activate\":true}"));
        Assert.Equal(0, IndependentPresentationQualification.Evaluate(positive).ContractCount);
    }
}
