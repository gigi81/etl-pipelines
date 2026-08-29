using EtlPipelines.Extensions.Sql.MySql;
using EtlPipelines.Extensions.Sql.Oracle;
using EtlPipelines.Extensions.Sql.SqlServer;

namespace EtlPipelines.Extensions.Sql.Tests;

/// <summary>
/// Splitting a script into the batches a server will take.
/// </summary>
/// <remarks>
/// Pure text work needing neither a database nor a file, which is why it is here rather than behind
/// a container: this is the part of running a script that engines disagree about, and every case
/// below is one that silently produces the wrong statements rather than an error when got wrong.
/// </remarks>
public sealed class ScriptParserTests
{
    private static async Task<string[]> ParseAsync(ISqlScriptParser parser, string sql)
    {
        using var reader = new StringReader(sql);

        var batches = new List<string>();

        await foreach (var batch in parser.ParseAsync(reader, CancellationToken.None))
        {
            batches.Add(batch.Trim());
        }

        return [.. batches];
    }

    [Test]
    public async Task Single_batch_hands_the_whole_file_over()
    {
        var batches = await ParseAsync(
            SingleBatchScriptParser.Instance,
            "CREATE TABLE a (Id INT);\nCREATE TABLE b (Id INT);");

        batches.Should().ContainSingle("PostgreSQL and SQLite take several statements in one command");
    }

    [Test]
    public async Task Single_batch_yields_nothing_for_an_empty_file()
    {
        (await ParseAsync(SingleBatchScriptParser.Instance, "   \n\n  ")).Should().BeEmpty();
    }

    [Test]
    [Arguments("GO")]
    [Arguments("go")]
    [Arguments("  Go  ")]
    public async Task Sql_server_ends_a_batch_on_a_line_of_nothing_but_go(string terminator)
    {
        var batches = await ParseAsync(
            new SqlServerScriptParser(),
            $"CREATE TABLE a (Id INT)\n{terminator}\nCREATE TABLE b (Id INT)\n");

        batches.Should().HaveCount(2);
        batches[0].Should().Be("CREATE TABLE a (Id INT)");
        batches[1].Should().Be("CREATE TABLE b (Id INT)");
    }

    [Test]
    public async Task Sql_server_drops_the_empty_batches_a_trailing_go_would_leave()
    {
        // Two terminators in a row, and one at the end of the file. The server rejects an empty
        // batch, so a parser that emitted them would break scripts that are perfectly valid.
        var batches = await ParseAsync(
            new SqlServerScriptParser(),
            "SELECT 1\nGO\nGO\nSELECT 2\nGO\n");

        batches.Should().Equal(["SELECT 1", "SELECT 2"]);
    }

    [Test]
    public async Task Sql_server_leaves_the_word_go_alone_inside_a_statement()
    {
        var batches = await ParseAsync(
            new SqlServerScriptParser(),
            "SELECT 'GO' AS Go\nGO\n");

        batches.Should().ContainSingle().Which.Should().Be("SELECT 'GO' AS Go");
    }

    [Test]
    public async Task Mysql_honours_a_delimiter_the_script_sets_for_itself()
    {
        // The reason the feature exists: a procedure body is full of semicolons, so the script
        // changes the delimiter first. Splitting on ";" regardless would cut the body into pieces.
        var batches = await ParseAsync(
            new MySqlScriptParser(),
            """
            DELIMITER $$
            CREATE PROCEDURE refresh()
            BEGIN
              DELETE FROM totals;
              INSERT INTO totals SELECT 1;
            END$$
            DELIMITER ;
            SELECT 1;
            """);

        batches.Should().HaveCount(2);
        batches[0].Should().Contain("CREATE PROCEDURE").And.Contain("DELETE FROM totals;");
        batches[1].Should().Be("SELECT 1");
    }

    [Test]
    public async Task Mysql_does_not_emit_a_trailing_batch_of_only_comments()
    {
        // A comment does not end with the delimiter, so it stays in the buffer. At the end of the
        // file there is no statement to carry it, and MySQL answers a comment-only batch with
        // ER_EMPTY_QUERY.
        var batches = await ParseAsync(
            new MySqlScriptParser(),
            "SELECT 1;\n-- all done\n# and this too\n");

        batches.Should().ContainSingle().Which.Should().Be("SELECT 1");
    }

    [Test]
    public async Task Oracle_keeps_the_semicolon_that_closes_a_plsql_block()
    {
        // Stripping the trailing semicolon off END; turns it into END, and the server rejects the
        // whole body. The terminator here is "/", which is what tells the parser this is PL/SQL.
        var batches = await ParseAsync(
            new OracleScriptParser(),
            """
            CREATE OR REPLACE PROCEDURE refresh AS
            BEGIN
              DELETE FROM totals;
            END;
            /
            """);

        batches.Should().ContainSingle();
        batches[0].Should().EndWith("END;");
    }

    [Test]
    public async Task Oracle_splits_plain_statements_on_the_semicolon()
    {
        var batches = await ParseAsync(
            new OracleScriptParser(),
            "CREATE TABLE a (Id NUMBER);\nCREATE TABLE b (Id NUMBER);\n");

        batches.Should().Equal(["CREATE TABLE a (Id NUMBER)", "CREATE TABLE b (Id NUMBER)"]);
    }

    [Test]
    public async Task Oracle_is_not_thrown_off_by_a_lone_apostrophe_in_a_comment()
    {
        // Counting quotes alone leaves the parser convinced it is still inside a string literal, and
        // every following statement gets swallowed into the same command. A line holding only the
        // terminator closes the statement regardless, which is what saves it.
        var batches = await ParseAsync(
            new OracleScriptParser(),
            """
            CREATE OR REPLACE PROCEDURE refresh AS
            BEGIN
              -- don't touch the totals
              NULL;
            END;
            /
            CREATE TABLE after_it (Id NUMBER);
            """);

        batches.Should().HaveCount(2);
        batches[1].Should().Be("CREATE TABLE after_it (Id NUMBER)");
    }
}
