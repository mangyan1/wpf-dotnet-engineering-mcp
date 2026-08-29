using EngineeringMcp.ControlCenter;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EngineeringMcp.IntegrationTests;

[TestClass]
public sealed class GearTrainLayoutTests
{
    [TestMethod]
    public void MeshedGears_HaveTangentPitchCirclesSynchronizedRatesAndComplementaryPhases()
    {
        var expectedRate = GearTrainLayout.ToothPassesPerSecond(GearTrainLayout.Specs[0]);

        foreach (var gear in GearTrainLayout.Specs)
            Assert.AreEqual(expectedRate, GearTrainLayout.ToothPassesPerSecond(gear), 0.000_001,
                "Every connected gear must pass teeth at the same rate.");

        foreach (var mesh in GearTrainLayout.Meshes)
        {
            Assert.AreNotEqual(GearTrainLayout.Specs[mesh.First].Ccw, GearTrainLayout.Specs[mesh.Second].Ccw,
                $"Gear pair {mesh.First}-{mesh.Second} must counter-rotate.");
            Assert.IsLessThan(0.01, GearTrainLayout.PitchCircleError(mesh),
                $"Gear pair {mesh.First}-{mesh.Second} must have tangent pitch circles.");
            Assert.IsLessThan(0.001, GearTrainLayout.PhaseError(mesh),
                $"Gear pair {mesh.First}-{mesh.Second} must place teeth opposite gaps.");
        }
    }
}
