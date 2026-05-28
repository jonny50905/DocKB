using DocumentKB.Core.Configuration;
using DocumentKB.Core.Odata;
using AwesomeAssertions;
using Xunit;

namespace DocumentKB.Core.Tests.Odata;

public class OdataQueryValidatorTests
{
    private readonly OdataQueryValidator _v =
        new(new OdataOptions { MaxTop = 200, DefaultTop = 50, MaxInputBytes = 4096 });

    [Fact] public void AllowedFunction_Passes()
        => _v.Validate("$filter=contains(FileName,'x')&$top=20").Should().BeNull();

    [Fact] public void BannedClause_apply_Rejected()
        => _v.Validate("$apply=groupby((FileType))")
            .Should().Contain("$apply");

    [Fact] public void BannedClause_search_Rejected()
        => _v.Validate("$search=foo").Should().Contain("$search");

    [Fact] public void BannedClause_compute_Rejected()
        => _v.Validate("$compute=Id mul 2 as X").Should().Contain("$compute");

    [Fact] public void TopOverMax_Rejected()
        => _v.Validate("$top=999").Should().Contain("$top");

    [Fact] public void InputTooLong_Rejected()
        => _v.Validate(new string('a', 5000)).Should().Contain("size");

    [Fact] public void ExpandDepthOverOne_Rejected()
        => _v.Validate("$expand=File($expand=Chunks)").Should().Contain("$expand");
}
