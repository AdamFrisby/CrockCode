using System;
using CrockCode.Core.Domain;
using Xunit;

namespace CrockCode.Tests;

public class ResultMonadTests
{
    [Fact]
    public void Result_Ok_Should_Match_Correct_Branch()
    {
        var result = Result.Ok(42);
        var matched = result.Match(
            ok => ok.ToString(),
            err => "error"
        );
        Assert.Equal("42", matched);
    }

    [Fact]
    public void Result_Err_Should_Match_Correct_Branch()
    {
        var errObj = new Error.Permanent("CODE", "Detail description");
        var result = Result.Err<int>(errObj);
        
        var matched = result.Match(
            ok => "success",
            err => err.Match(t => t.Detail, p => p.Detail)
        );
        Assert.Equal("Detail description", matched);
    }

    [Fact]
    public void Map_Should_Transform_Value_On_Ok()
    {
        var result = Result.Ok(10).Map(x => x * 2);
        Assert.Equal(20, result.Unwrap());
    }

    [Fact]
    public void Map_Should_Propagate_Error_On_Err()
    {
        var errObj = new Error.Permanent("ERR", "Msg");
        var result = Result.Err<int>(errObj).Map(x => x * 2);
        
        Assert.True(result.IsErr);
        Assert.Equal(errObj, result.UnwrapErr());
    }

    [Fact]
    public void Bind_Should_Chain_Monadic_Operations_On_Ok()
    {
        var result = Result.Ok(5).Bind(x => Result.Ok(x + 10));
        Assert.Equal(15, result.Unwrap());
    }

    [Fact]
    public void Bind_Should_Propagate_Error_On_Chained_Err()
    {
        var errObj = new Error.Transient("ERR", "Msg");
        var result = Result.Ok(5).Bind(x => Result.Err<int>(errObj));
        
        Assert.True(result.IsErr);
        Assert.Equal(errObj, result.UnwrapErr());
    }

    [Fact]
    public void Linq_Syntax_Should_Compose_Multiple_Results_Successfully()
    {
        var result = from x in Result.Ok(10)
                     from y in Result.Ok(20)
                     select x + y;

        Assert.Equal(30, result.Unwrap());
    }

    [Fact]
    public void Linq_Syntax_Should_Short_Circuit_On_First_Error()
    {
        var errObj = new Error.Permanent("FIRST", "fail");
        var result = from x in Result.Err<int>(errObj)
                     from y in Result.Ok(20)
                     select x + y;

        Assert.True(result.IsErr);
        Assert.Equal(errObj, result.UnwrapErr());
    }

    [Fact]
    public void Linq_Syntax_Should_Short_Circuit_On_Second_Error()
    {
        var errObj = new Error.Permanent("SECOND", "fail");
        var result = from x in Result.Ok(10)
                     from y in Result.Err<int>(errObj)
                     select x + y;

        Assert.True(result.IsErr);
        Assert.Equal(errObj, result.UnwrapErr());
    }
}
