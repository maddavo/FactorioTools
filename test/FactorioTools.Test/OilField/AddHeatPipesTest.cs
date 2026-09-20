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
}
