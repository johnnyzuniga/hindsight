using ResMan.Core;

namespace ResMan.Data;

public sealed class FixStore(ResManDb db)
{
    public long Add(FixAttempt f)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO fix_attempts(issue_key,problem_kind,app,label,applied_utc) VALUES($i,$k,$a,$l,$t);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$i", f.IssueKey);
        cmd.Parameters.AddWithValue("$k", f.ProblemKind);
        cmd.Parameters.AddWithValue("$a", (object?)f.App ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$l", f.Label);
        cmd.Parameters.AddWithValue("$t", ResManDb.ToMs(f.AppliedUtc));
        return (long)cmd.ExecuteScalar()!;
    }

    public IReadOnlyList<FixAttempt> List()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,issue_key,problem_kind,app,label,applied_utc FROM fix_attempts ORDER BY applied_utc DESC";
        var list = new List<FixAttempt>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new FixAttempt(r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.GetString(4), ResManDb.FromMs(r.GetInt64(5))));
        return list;
    }
}
