namespace ContextWindow.Tests;

internal static class Schema
{
    internal static Task ResetAsync() => Database.ExecuteAsync("""
        DROP SCHEMA IF EXISTS unknown_ledger CASCADE;
        DROP SCHEMA IF EXISTS unknown_downstream CASCADE;
        CREATE SCHEMA unknown_ledger;
        CREATE SCHEMA unknown_downstream;
        CREATE TABLE unknown_ledger.intents(tenant text NOT NULL,intent text NOT NULL,operation text NOT NULL,PRIMARY KEY(tenant,intent));
        CREATE TABLE unknown_ledger.operations(
          tenant text NOT NULL,operation text NOT NULL,args text NOT NULL,payload text NOT NULL,capability text NOT NULL,
          state text NOT NULL DEFAULT 'prepared' CHECK(state IN ('prepared','dispatching','unknown','succeeded')),
          receipt text,owner text,version bigint NOT NULL DEFAULT 0,history text[] NOT NULL DEFAULT ARRAY['prepared'],
          replay_until timestamptz NOT NULL DEFAULT now()+interval '1 day',
          PRIMARY KEY(tenant,operation),CHECK((state='succeeded')=(receipt IS NOT NULL)));
        CREATE TABLE unknown_downstream.requests(id bigserial PRIMARY KEY,tenant text NOT NULL,operation text NOT NULL,kind text NOT NULL);
        CREATE TABLE unknown_downstream.mutations(receipt text PRIMARY KEY,tenant text NOT NULL,operation text NOT NULL,payload text NOT NULL);
        CREATE TABLE unknown_downstream.keys(tenant text NOT NULL,operation text NOT NULL,args text NOT NULL,receipt text NOT NULL,PRIMARY KEY(tenant,operation));
        """);
}
