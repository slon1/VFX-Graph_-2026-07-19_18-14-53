using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

[TestFixture]
public class SpatialHashValidatorTests
{
    [Test]
    public void Valid_WrapWithMatchingBounds()
    {
        IReadOnlyList<string> warnings = SpatialHashValidator.Validate(new SimPass[]
        {
            new BuildSpatialHashPass(),
            MatchingBounds(),
        });
        Assert.AreEqual(0, warnings.Count);
    }

    [Test]
    public void TwoBuilders_Throw()
    {
        Assert.Throws<InvalidOperationException>(() => SpatialHashValidator.Validate(new SimPass[]
        {
            new BuildSpatialHashPass(),
            MatchingBounds(),
            new BuildSpatialHashPass(),
        }));
    }

    [Test]
    public void DisabledSecondBuilder_Ok()
    {
        BuildSpatialHashPass second = new BuildSpatialHashPass { Enabled = false };
        IReadOnlyList<string> warnings = SpatialHashValidator.Validate(new SimPass[]
        {
            new BuildSpatialHashPass(),
            MatchingBounds(),
            second,
        });
        Assert.AreEqual(0, warnings.Count);
    }

    [Test]
    public void ConsumerBeforeBuilder_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => SpatialHashValidator.Validate(new SimPass[]
        {
            new FakeHashConsumerPass(),
            new BuildSpatialHashPass(),
            MatchingBounds(),
        }));
    }

    [Test]
    public void ConsumerWithoutBuilder_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => SpatialHashValidator.Validate(new SimPass[]
        {
            new FakeHashConsumerPass(),
        }));
    }

    [Test]
    public void ConsumerAfterBuilder_Ok()
    {
        IReadOnlyList<string> warnings = SpatialHashValidator.Validate(new SimPass[]
        {
            new BuildSpatialHashPass(),
            MatchingBounds(),
            new FakeHashConsumerPass(),
        });
        Assert.AreEqual(0, warnings.Count);
    }

    [Test]
    public void Wrap_NoBoxBounds_Throws()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => SpatialHashValidator.Validate(new SimPass[] { new BuildSpatialHashPass() }));
        StringAssert.IsMatch("phantom|BoxBounds", exception.Message);
    }

    [Test]
    public void Wrap_DisabledBoxBounds_Throws()
    {
        BoxBoundsPass bounds = MatchingBounds();
        bounds.Enabled = false;
        Assert.Throws<InvalidOperationException>(() => SpatialHashValidator.Validate(new SimPass[]
        {
            new BuildSpatialHashPass(),
            bounds,
        }));
    }

    [Test]
    public void Wrap_BounceBoxBounds_Throws()
    {
        BoxBoundsPass bounds = MatchingBounds();
        bounds.Behaviour = BoundsBehaviour.Bounce;
        Assert.Throws<InvalidOperationException>(() => SpatialHashValidator.Validate(new SimPass[]
        {
            new BuildSpatialHashPass(),
            bounds,
        }));
    }

    [Test]
    public void ExtentsMismatch_Throws()
    {
        BoxBoundsPass off = MatchingBounds();
        off.Extents = new Vector3(16.01f, 0f, 16f);
        Assert.Throws<InvalidOperationException>(() => SpatialHashValidator.Validate(new SimPass[]
        {
            new BuildSpatialHashPass(),
            off,
        }));

        BoxBoundsPass close = MatchingBounds();
        close.Extents = new Vector3(16f + 1e-4f, 0f, 16f);
        Assert.DoesNotThrow(() => SpatialHashValidator.Validate(new SimPass[]
        {
            new BuildSpatialHashPass(),
            close,
        }));
    }

    [Test]
    public void CenterMismatch_Throws()
    {
        BoxBoundsPass off = MatchingBounds();
        off.Center = new Vector3(0.01f, 0f, 0f);
        Assert.Throws<InvalidOperationException>(() => SpatialHashValidator.Validate(new SimPass[]
        {
            new BuildSpatialHashPass(),
            off,
        }));

        BoxBoundsPass close = MatchingBounds();
        close.Center = new Vector3(1e-4f, 0f, 0f);
        Assert.DoesNotThrow(() => SpatialHashValidator.Validate(new SimPass[]
        {
            new BuildSpatialHashPass(),
            close,
        }));
    }

    [Test]
    public void ExtentsY_Ignored()
    {
        BuildSpatialHashPass builder = new BuildSpatialHashPass
        {
            Extents = new Vector3(16f, 0f, 16f),
        };
        BoxBoundsPass bounds = MatchingBounds();
        bounds.Extents = new Vector3(16f, 5f, 16f);
        Assert.DoesNotThrow(() => SpatialHashValidator.Validate(new SimPass[] { builder, bounds }));
    }

    [Test]
    public void NoWrap_NoBoxBounds_Warns()
    {
        BuildSpatialHashPass builder = new BuildSpatialHashPass { Wrap = false };
        IReadOnlyList<string> warnings = SpatialHashValidator.Validate(new SimPass[] { builder });
        Assert.AreEqual(1, warnings.Count);
        StringAssert.Contains("particles may leave the hash grid and pile up in edge cells", warnings[0]);
    }

    [Test]
    public void NoWrap_WrapBoxBounds_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => SpatialHashValidator.Validate(new SimPass[]
        {
            new BuildSpatialHashPass { Wrap = false },
            MatchingBounds(),
        }));
    }

    [Test]
    public void LayoutError_Propagates()
    {
        BuildSpatialHashPass builder = new BuildSpatialHashPass
        {
            Extents = new Vector3(2f, 0f, 2f),
            MinCellSize = 2f,
            Wrap = true,
        };
        Assert.Throws<InvalidOperationException>(() => SpatialHashValidator.Validate(new SimPass[]
        {
            builder,
            new BoxBoundsPass
            {
                Center = Vector3.zero,
                Extents = new Vector3(2f, 0f, 2f),
                Behaviour = BoundsBehaviour.Wrap,
            },
        }));
    }

    private static BoxBoundsPass MatchingBounds()
    {
        return new BoxBoundsPass
        {
            Center = Vector3.zero,
            Extents = new Vector3(16f, 0f, 16f),
            Behaviour = BoundsBehaviour.Wrap,
        };
    }

    private sealed class FakeHashConsumerPass : SimPass, ISpatialHashConsumer
    {
        public override string DisplayName => "Fake Hash Consumer";
        public override PassCategory Category => PassCategory.Force;
        public override IReadOnlyList<AttributeId> Reads => AttrSets.None;
        public override IReadOnlyList<AttributeId> Writes => AttrSets.None;
        public override void Initialize(SimContext context)
        {
        }

        public override void Execute(SimContext context, float deltaTime)
        {
        }
    }
}
