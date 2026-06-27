module Nameless.TaskList.IntegrationTests.WhatsAppListenerTests

open System.Threading
open Npgsql
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Core.Adapters
open Nameless.TaskList.IntegrationTests.Support
open Xunit

[<SkippableFact>]
let ``NpgsqlNotificationListener receives a payload published on its channel`` () =
    Skip.IfNot(ServiceProbes.postgres.Value, "Postgres not available")
    let listener = new NpgsqlNotificationListener(Config.connectionString) :> INotificationListener
    listener.Subscribe "whatsapp_new_message"
    // Publish from a second connection.
    use pub = new NpgsqlConnection(Config.connectionString)
    pub.Open()
    use cmd = new NpgsqlCommand("""NOTIFY whatsapp_new_message, '{"id":"IT1","chat_jid":"c@s","timestamp":"2026-06-18T10:00:00+02:00"}'""", pub)
    cmd.ExecuteNonQuery() |> ignore
    // WaitNext should surface it (use a timeout token so a failure doesn't hang the suite).
    use cts = new CancellationTokenSource(System.TimeSpan.FromSeconds 10.0)
    let payloads = listener.WaitNext cts.Token
    Assert.Contains(payloads, fun (p: string) -> p.Contains "IT1")
