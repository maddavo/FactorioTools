using Knapcode.FactorioTools.Data;

namespace Knapcode.FactorioTools.OilField;

public class AddHeatPipesTest : BasePlannerTest
{
    [Fact]
    public void AddsConnectedHeatPipesToAnAquiloPlan()
    {
        var options = OilFieldOptions.ForMediumElectricPole;
        options.AddHeatPipes = true;
        options.ValidateSolution = true;
        var blueprint = ParseBlueprint.Execute(SmallListBlueprintStrings[0]);

        var (context, _) = Planner.Execute(options, blueprint);
        var outputBlueprint = ParseBlueprint.Execute(GridToBlueprintString.Execute(context, addFbeOffset: false, addAvoidEntities: false));

        var expectedCount = context.Grid.GetEntities().OfType<HeatPipe>().Count();
        Assert.NotEqual(0, expectedCount);
        Assert.Equal(expectedCount, outputBlueprint.Entities.Count(entity => entity.Name == EntityNames.Vanilla.HeatPipe));
        Assert.Contains(context.Grid.EntityLocations.EnumerateItems(), location =>
            context.Grid[location] is HeatPipe
            && (location.X == 0 || location.Y == 0 || location.X == context.Grid.Width - 1 || location.Y == context.Grid.Height - 1));
    }

    [Fact]
    public void DoesNotAddHeatPipesUnlessRequested()
    {
        var options = OilFieldOptions.ForMediumElectricPole;
        options.ValidateSolution = true;
        var blueprint = ParseBlueprint.Execute(SmallListBlueprintStrings[0]);

        var (context, _) = Planner.Execute(options, blueprint);

        Assert.Empty(context.Grid.GetEntities().OfType<HeatPipe>());
    }

    [Fact]
    public void AddsHeatPipesBeforeSubstationsCanBlockTheRoute()
    {
        var options = OilFieldOptions.ForSubstation;
        options.AddHeatPipes = true;
        options.ValidateSolution = true;

        var (context, _) = Planner.Execute(options, ParseBlueprint.Execute(SmallListBlueprintStrings[0]));

        Assert.NotEmpty(context.Grid.GetEntities().OfType<HeatPipe>());
    }

    [Theory]
    [MemberData(nameof(SmallListIndexTestData))]
    public void HeatPipeConstraintAlwaysWorksWithSubstations(int blueprintIndex)
    {
        var options = OilFieldOptions.ForSubstation;
        options.AddHeatPipes = true;
        options.ValidateSolution = true;

        var (context, _) = Planner.Execute(options, ParseBlueprint.Execute(SmallListBlueprintStrings[blueprintIndex]));

        Assert.NotEmpty(context.Grid.GetEntities().OfType<HeatPipe>());
    }

    [Theory]
    [InlineData(38)]
    [InlineData(41)]
    [InlineData(48)]
    [InlineData(60)]
    public void PreviouslyBlockedLayoutsHaveHeatRoutesWithoutBeacons(int blueprintIndex)
    {
        var options = OilFieldOptions.ForSubstation;
        options.AddBeacons = false;
        options.AddHeatPipes = true;
        options.ValidateSolution = true;

        var (context, _) = Planner.Execute(options, ParseBlueprint.Execute(SmallListBlueprintStrings[blueprintIndex]));

        Assert.NotEmpty(context.Grid.GetEntities().OfType<HeatPipe>());
    }
}
