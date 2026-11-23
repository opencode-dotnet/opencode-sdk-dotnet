using OpenCode.Playground.QuickStart;
using OpenCode.Sdk.Models;

namespace OpenCode.Playground.Hq.Tests.QuickStart;

/// <summary>
/// QuickStart's free-model filter, the one the root README's streaming example uses: a model of
/// opencode's own provider whose price list is not empty and charges nothing for input and output
/// on every tier.
/// </summary>
public sealed class QuickStartFreeModelTests
{
    [Test]
    public async Task IsFree_Should_Accept_An_Opencode_Model_That_Charges_Nothing()
    {
        await Assert.That(FreeModel.IsFree(Model("opencode", Cost(0, 0)))).IsTrue();
    }

    [Test]
    public async Task IsFree_Should_Reject_A_Model_With_No_Listed_Price()
    {
        await Assert.That(FreeModel.IsFree(Model("opencode"))).IsFalse();
    }

    [Test]
    public async Task IsFree_Should_Reject_A_Model_That_Charges_For_Output()
    {
        await Assert.That(FreeModel.IsFree(Model("opencode", Cost(0, 15)))).IsFalse();
    }

    [Test]
    public async Task IsFree_Should_Reject_A_Model_With_A_Priced_Tier()
    {
        await Assert.That(FreeModel.IsFree(Model("opencode", Cost(0, 0), Cost(3, 0)))).IsFalse();
    }

    [Test]
    public async Task IsFree_Should_Reject_Another_Provider()
    {
        await Assert.That(FreeModel.IsFree(Model("anthropic", Cost(0, 0)))).IsFalse();
    }

    private static ModelCost Cost(double input, double output) =>
        new() { Input = input, Output = output, Cache = new ModelCostCache { Read = 0, Write = 0 } };

    private static ModelInfo Model(string providerId, params ModelCost[] cost) => new()
    {
        Id = "model",
        ModelId = "model",
        ProviderId = providerId,
        Name = "Model",
        Capabilities = new ModelCapabilities { Tools = true, Input = ["text"], Output = ["text"] },
        Variants = [],
        Time = new ModelInfoTime { Released = 0 },
        Cost = cost,
        Status = ModelInfoStatus.Active,
        Enabled = true,
        Limit = new ModelInfoLimit { Context = 1, Output = 1 },
    };
}
