using System.Text.Json;
using ResMan.Core;

namespace ResMan.Data;

/// <summary>Versioned rule storage. Every change is recorded in rule_history so it can be undone.</summary>
public sealed class RuleStore(ResManDb db)
{
    public long Add(RuleProposal rule)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO rules(process,action,value,profile,reason,enabled,created_utc) VALUES($p,$a,$v,$pr,$r,1,$t);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$p", rule.Process);
        cmd.Parameters.AddWithValue("$a", rule.Action);
        cmd.Parameters.AddWithValue("$v", rule.Value);
        cmd.Parameters.AddWithValue("$pr", rule.Profile);
        cmd.Parameters.AddWithValue("$r", rule.Reason);
        cmd.Parameters.AddWithValue("$t", ResManDb.ToMs(DateTime.UtcNow));
        var id = (long)cmd.ExecuteScalar()!;
        History(c, tx, id, "added", rule);
        tx.Commit();
        return id;
    }

    public bool Remove(long id)
    {
        var existing = List().FirstOrDefault(r => r.Id == id);
        if (existing is null) return false;
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM rules WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
        History(c, tx, id, "removed", existing.Rule);
        tx.Commit();
        return true;
    }

    public List<StoredRule> List()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,process,action,value,profile,reason,enabled,created_utc FROM rules ORDER BY id";
        var list = new List<StoredRule>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new StoredRule(r.GetInt64(0),
                new RuleProposal(r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5)),
                r.GetInt64(6) != 0, ResManDb.FromMs(r.GetInt64(7))));
        return list;
    }

    private static void History(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction tx, long ruleId, string change, RuleProposal rule)
    {
        using var h = c.CreateCommand();
        h.Transaction = tx;
        h.CommandText = "INSERT INTO rule_history(rule_id,change,time_utc,rule_json) VALUES($id,$c,$t,$j)";
        h.Parameters.AddWithValue("$id", ruleId);
        h.Parameters.AddWithValue("$c", change);
        h.Parameters.AddWithValue("$t", ResManDb.ToMs(DateTime.UtcNow));
        h.Parameters.AddWithValue("$j", JsonSerializer.Serialize(rule));
        h.ExecuteNonQuery();
    }
}
