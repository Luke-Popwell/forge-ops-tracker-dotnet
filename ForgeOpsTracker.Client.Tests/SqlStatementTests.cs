namespace ForgeOpsTracker.Tests;

// Mirrors gems/forge_ops_tracker/spec/forge_ops_tracker/sql_statement_spec.rb.
public class SqlStatementTests
{
    // Stands in for Npgsql's PostgresException, which exposes the statement as .Statement.
    private sealed class FakePostgresException : Exception
    {
        public FakePostgresException(string message, string? statement) : base(message) => Statement = statement;

        public string? Statement { get; }
    }

    // Stands in for a wrapper exposing the statement as .CommandText.
    private sealed class FakeCommandException : Exception
    {
        public FakeCommandException(string commandText) : base("boom") => CommandText = commandText;

        public string CommandText { get; }
    }

    private static EventBuilder Builder(bool objects = true, bool statement = false) => new(new Configuration
    {
        Environment = "production",
        CaptureSqlObjects = objects,
        CaptureSqlStatement = statement
    });

    [Fact]
    public void FindsTheStatementViaStatementOrCommandTextProperties()
    {
        Assert.Equal("SELECT 1", SqlStatement.FindIn(new FakePostgresException("boom", "SELECT 1")));
        Assert.Equal("SELECT 2", SqlStatement.FindIn(new FakeCommandException("SELECT 2")));
    }

    [Fact]
    public void ReadsTheExplicitDataKeyOffAnyExceptionType()
    {
        var tagged = new InvalidOperationException("boom");
        tagged.Data[SqlStatement.ExplicitDataKey] = "SELECT 9";
        Assert.Equal("SELECT 9", SqlStatement.FindIn(tagged));
        Assert.Equal("SELECT 9", SqlStatement.FindIn(new Exception("wrapped", tagged)));
    }

    [Fact]
    public void WalksInnerExceptionsAndAggregateExceptions()
    {
        var wrapped = new InvalidOperationException("refund failed", new FakePostgresException("db", "CALL refund_order(1)"));
        Assert.Equal("CALL refund_order(1)", SqlStatement.FindIn(wrapped));

        var aggregate = new AggregateException(new Exception("a"), new FakePostgresException("db", "SELECT 3"));
        Assert.Equal("SELECT 3", SqlStatement.FindIn(aggregate));
    }

    [Fact]
    public void ReturnsNullWithoutSql()
    {
        Assert.Null(SqlStatement.FindIn(new InvalidOperationException("nope")));
        Assert.Null(SqlStatement.FindIn(new FakePostgresException("blank", "  ")));
        Assert.Null(SqlStatement.FindIn(null));
    }

    [Fact]
    public void MasksStringsAndNumbersButNotIdentifiersOrPlaceholders()
    {
        Assert.Equal(
            "SELECT * FROM orders2 WHERE email = ? AND id = ? AND x = $1",
            SqlStatement.MaskStatement("SELECT * FROM orders2 WHERE email = 'a@b.co' AND id = 42 AND x = $1"));
        Assert.Equal("SELECT price * ? FROM t", SqlStatement.MaskStatement("SELECT price * 1.5 FROM t"));
    }

    [Fact]
    public void MasksAnEscapedQuoteACutOffStringAndADollarQuotedBody()
    {
        Assert.Equal("EXEC sp_x @t = ?", SqlStatement.MaskStatement("EXEC sp_x @t = 'it''s'"));
        Assert.Equal("SELECT ? WHERE n = ?", SqlStatement.MaskStatement("SELECT 1 WHERE n = 'oops"));
        Assert.Equal("DO ?", SqlStatement.MaskStatement("DO $b$ BEGIN PERFORM 1; END $b$"));
    }

    [Fact]
    public void IsIdempotentTruncatesAndReturnsNullForBlank()
    {
        var once = SqlStatement.MaskStatement("SELECT * FROM t WHERE a = 'x' AND b = 9");
        Assert.Equal(once, SqlStatement.MaskStatement(once));
        Assert.Equal(SqlStatement.MaxLength + 3, SqlStatement.MaskStatement("SELECT " + string.Concat(Enumerable.Repeat("a, ", 3000)) + " b")!.Length);
        Assert.Null(SqlStatement.MaskStatement("  "));
        Assert.Null(SqlStatement.MaskStatement(null));
    }

    // The server's own test set for SqlStatementMasker: input, db.system (or null), expected.
    [Theory]
    [InlineData("SELECT * FROM orders WHERE email = 'a@b.co' AND id = 42 LIMIT 10", null, "SELECT * FROM orders WHERE email = ? AND id = ? LIMIT ?")]
    [InlineData("EXEC sp_note @text = 'it''s broken'", null, "EXEC sp_note @text = ?")]
    [InlineData("SELECT 1 WHERE name = 'unterminated", null, "SELECT ? WHERE name = ?")]
    [InlineData("DO $body$ BEGIN PERFORM 1; END $body$", null, "DO ?")]
    [InlineData("SELECT \"user id\" FROM orders2 WHERE id = $1 AND v = sp_v2(?)", null, "SELECT \"user id\" FROM orders2 WHERE id = $1 AND v = sp_v2(?)")]
    [InlineData("SELECT price * 1.5 FROM t", null, "SELECT price * ? FROM t")]
    [InlineData("SELECT * FROM users WHERE name = E'o\\'brien' AND id = 1", null, "SELECT * FROM users WHERE name = ? AND id = ?")]
    [InlineData("SELECT * FROM users WHERE name = 'o\\'brien' AND id = 1", null, "SELECT * FROM users WHERE name = ? AND id = ?")]
    [InlineData("SELECT * FROM t WHERE b = X'DEADBEEF' AND s = N'uni' AND u = U&'d\\0061t' AND e = e'x'", null, "SELECT * FROM t WHERE b = ? AND s = ? AND u = ? AND e = ?")]
    [InlineData("SELECT * FROM t WHERE a LIKE'%secret%'", null, "SELECT * FROM t WHERE a LIKE?")]
    [InlineData("SELECT * FROM t WHERE f = 0x1F AND b = 0b101 AND n = 3e10 AND m = 1.5E-3 AND k = .5", null, "SELECT * FROM t WHERE f = ? AND b = ? AND n = ? AND m = ? AND k = ?")]
    [InlineData("SELECT e, t.col, 1e5e FROM t", null, "SELECT e, t.col, 1e5e FROM t")]
    [InlineData("SELECT \"user id\" FROM t WHERE token = \"abc123secret\"", "mysql", "SELECT ? FROM t WHERE token = ?")]
    [InlineData("SELECT \"user id\" FROM t WHERE token = \"abc123secret\"", "MariaDB", "SELECT ? FROM t WHERE token = ?")]
    [InlineData("SELECT \"user id\" FROM t WHERE token = \"abc123secret\"", "postgresql", "SELECT \"user id\" FROM t WHERE token = \"abc123secret\"")]
    [InlineData("SELECT \"user id\" FROM t WHERE token = \"abc123secret\"", null, "SELECT \"user id\" FROM t WHERE token = \"abc123secret\"")]
    [InlineData("SELECT * FROM t WHERE a = 'x' AND b = 9", null, "SELECT * FROM t WHERE a = ? AND b = ?")]
    [InlineData("SELECT * FROM t WHERE a = ? AND b = ?", null, "SELECT * FROM t WHERE a = ? AND b = ?")]
    [InlineData("SELECT * FROM t WHERE path = 'C:\\\\dir\\\\' AND n = 5", null, "SELECT * FROM t WHERE path = ? AND n = ?")]
    [InlineData("INSERT INTO t (a, b) VALUES (-5, +3.25e+2)", null, "INSERT INTO t (a, b) VALUES (-?, +?)")]
    [InlineData("SELECT * FROM t WHERE a = 'secret\\", null, "SELECT * FROM t WHERE a = ?")]
    [InlineData("SELECT * FROM t WHERE a = \"secret\\", "mysql", "SELECT * FROM t WHERE a = ?")]
    public void MatchesTheServersMaskerOnEverySharedCase(string input, string? system, string expected)
    {
        Assert.Equal(expected, SqlStatement.MaskStatement(input, system));
        Assert.Equal(expected, SqlStatement.MaskStatement(expected, system));
    }

    [Fact]
    public void MasksAVeryLongStringInOnePiece()
    {
        Assert.Equal("SELECT ?", SqlStatement.MaskStatement("SELECT '" + new string('x', 100_000) + "'"));
        Assert.Equal("SELECT ?", SqlStatement.MaskStatement("SELECT '" + string.Concat(Enumerable.Repeat("x\\'", 50_000)) + "'"));
        Assert.Equal("SELECT ?", SqlStatement.MaskStatement("SELECT \"" + new string('x', 100_000) + "\"", "mysql"));
    }

    [Fact]
    public void FindsAStoredProcedureWithItsSchema()
    {
        var found = SqlStatement.Objects("EXEC dbo.sp_refund_order @id = ?")!;
        Assert.Equal("EXEC", found["operation"]);
        Assert.Equal(new List<string> { "dbo.sp_refund_order" }, found["procedures"]);
        Assert.Equal(new List<string>(), found["relations"]);
        Assert.Equal(new List<string> { "refund_order" }, SqlStatement.Objects("CALL refund_order(?, ?)")!["procedures"]);
        Assert.Equal(new List<string> { "refund_order" }, SqlStatement.Objects("SELECT refund_order(?, ?)")!["procedures"]);
    }

    [Fact]
    public void FindsViewsJoinedTablesAndTableFunctions()
    {
        Assert.Equal(
            new List<string> { "v_totals", "public.customers" },
            SqlStatement.Objects("SELECT * FROM v_totals t JOIN public.customers c ON c.id = t.id")!["relations"]);
        Assert.Equal(new List<string> { "get_open_orders" }, SqlStatement.Objects("SELECT * FROM get_open_orders(?) o")!["procedures"]);
    }

    [Fact]
    public void DoesNotMisreadColumnListsOrBuiltinsAndReturnsNullForGarbage()
    {
        Assert.Equal(new List<string>(), SqlStatement.Objects("INSERT INTO audit_log (a) VALUES (?)")!["procedures"]);
        Assert.Equal(new List<string>(), SqlStatement.Objects("SELECT count(*) FROM orders")!["procedures"]);
        Assert.Null(SqlStatement.Objects("garbage"));
    }

    [Fact]
    public void EventBuilderSendsTheProcedureNameButNotTheStatementByDefault()
    {
        var error = new FakePostgresException("boom", "EXEC dbo.sp_refund_order @order_id = 8814, @note = 'a@b.co'");

        var evt = Builder().Build(error);

        Assert.Equal(new List<string> { "dbo.sp_refund_order" }, evt.SqlObjects!["procedures"]);
        Assert.Null(evt.SqlStatement);
    }

    [Fact]
    public void EventBuilderSendsTheMaskedStatementWhenOptedInAndNothingWhenOff()
    {
        var error = new FakePostgresException("boom", "EXEC dbo.sp_refund_order @order_id = 8814, @note = 'a@b.co'");

        Assert.Equal("EXEC dbo.sp_refund_order @order_id = ?, @note = ?", Builder(true, true).Build(error).SqlStatement);

        var off = Builder(false, false).Build(error);
        Assert.Null(off.SqlObjects);
        Assert.Null(off.SqlStatement);
        Assert.Null(Builder().Build(new InvalidOperationException("nope")).SqlObjects);
    }
}
