using ShortForge.Core;
using Xunit;

namespace ShortForge.Core.Tests;

public sealed class CropTrajectoryTests
{
    private static FaceBox Face(double x, double y = .4) => new(x, y, .1, .12);

    [Fact]
    public void SmoothsMovingFaceInDisplaySpaceAndRestartsAtCuts()
    {
        var samples = new List<FaceSample>();
        for (int i = 0; i < 10; i++)
            samples.Add(new(5_000_000 + i * 200_000, false, [Face(.12 + i * .06)]));
        samples.Add(new(7_000_000, true, [Face(0)]));
        var path = CropTrajectory.Build(samples, 5_000_000, 7_200_000, 16d/9, 200);
        Assert.True(path.Fits(2_200_000));
        double visible = (9d / 16) / (16d / 9);
        for (int i = 0; i < 10; i++)
        {
            var p = path.Points[i];
            Assert.Equal("crop", p.Frame);
            Assert.Equal(i * 200_000L, p.TimeUs);
            double left = (1 - visible) * p.X;
            var face = samples[i].Faces[0];
            Assert.InRange(face.X, left, left + visible - face.Width);
            if (i > 0)
                Assert.InRange((p.X - path.Points[i - 1].X) * (1 - visible), 0, .55 * .2);
        }
        Assert.True(path.Points[^1].Cut);
        Assert.Equal(0d, path.Points[^1].X);
        Assert.Equal(new(11,0,0,1), path.Counts);
    }

    [Fact]
    public void MissingFacesHoldBrieflyAndMultiPersonScenesAlwaysFit()
    {
        FaceSample[] samples = [
            new(0, false, []), new(250_000, false, [Face(.3)]), new(500_000, false, []),
            new(750_000, false, []), new(1_000_000, false, []), new(1_250_000, false, []),
            new(1_500_000, true, [Face(.2)]), new(1_750_000, false, [Face(.1), Face(.65)]),
            new(2_000_000, false, [Face(.65)]), new(2_250_000, true, [Face(.15)])];
        var path = CropTrajectory.Build(samples, 0, 2_500_000, 1.77, 250);
        Assert.Equal(new[] {"fit","crop","crop","crop","crop","fit","fit","fit","fit","crop"},
            path.Points.Select(p => p.Frame));
        Assert.All(path.Points[2..5], p => Assert.Equal(path.Points[1].X, p.X));
        Assert.Equal(new(4,1,5,2), path.Counts);
    }

    [Fact]
    public void UnreachableFaceAcrossTheFrameKeepsWholeShotStaticUntilCut()
    {
        var path = CropTrajectory.Build([new(0, false, [Face(0)]), new(100_000, false, [Face(.89)]),
            new(200_000, true, [Face(.89)])], 0, 300_000, 16d/9, 100);
        Assert.Equal(new[] {"fit","fit","crop"}, path.Points.Select(p => p.Frame));
        Assert.Equal(1, path.Points[^1].X);
    }

    [Fact]
    public void PortraitAndInvalidCoordinatesRespectVerticalBounds()
    {
        var p = CropTrajectory.Build([new(0, false, [Face(.4, .88)])], 0, 500_000, .3, 100).Points[0];
        Assert.Equal(.5, p.X);
        Assert.Equal(1, p.Y);
        Assert.Throws<UserError>(() => CropTrajectory.Build([new(0, false, [Face(double.NaN)])], 0, 100, 1, 200));
        Assert.Throws<UserError>(() => CropTrajectory.Build([new(0,false,[]),new(0,false,[])], 0, 100, 1, 200));
        Assert.False(new CropTrajectory(200, [new(0,"crop",.5,.5,false),new(0,"fit",.5,.5,false)],
            new(1,0,1,0)).Fits(10));
    }
}
