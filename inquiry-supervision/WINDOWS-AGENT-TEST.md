# Windows Agent: manual test of "Confirm Resolved" and "Not Resolved Yet"

**Why this is manual:** the Windows Agent is a WPF desktop application. It cannot run in the Linux container
this stack was built in, so the two buttons have **not** been clicked in the real application. What *was* verified,
live, is everything behind them: the Agent protocol and the requests the buttons send (see `live_agent.py` and
PROGRESS.md §9). This procedure covers the part that remains: the window itself.

Estimated time: 20 minutes on a Windows 10/11 PC that can reach the PC running Docker.

## 0. Prepare (once)

1. On the Docker PC the stack must be running (`http://localhost:8190/health/live` returns 200).
2. Build or copy the Agent. From the repo on a Windows machine with the .NET 8 SDK:
   `dotnet build windows-client/src/Iemas.Agent/Iemas.Agent.csproj -c Release`
   Run `windows-client/src/Iemas.Agent/bin/Release/net8.0-windows/Iemas.Agent.exe`.
3. In the Agent's settings enter **Server URL** `http://<Docker PC address>:8190` (use `http://localhost:8190` on the Docker PC itself).
   Plain `http` is fine for this test network; the Agent's default is `https://localhost:8443`, which is the *old* stack's proxy.
4. CMS (`http://localhost:8191`): the employee for the agent must exist (Employees) with the same email address you enter in the Agent.

## 1. Register and approve the device

1. In the Agent enter the agent's enrolled email (for example `agent1@sawo.com`) and press register.
2. In the CMS open **People & Devices → Windows Agents**, select the pending device, choose the matching employee and **Approve**.
3. The Agent should switch to *Connected*. **Expected:** the main window lists the agent's inquiries.

## 2. Create an inquiry that the AI marks "Potentially resolved"

The simplest reliable way is the same as the live test (needs the AI stand-in or a real OpenRouter key):

1. Start the AI stand-in: `docker run -d --name iemas-inq-mockai --network iemas-inq_default --network-alias mockai -v <repo>/inquiry-supervision/live-test:/t:ro python:3.12-alpine python /t/mock_openrouter.py`
   and in the CMS **AI Configuration → AI Models → OpenRouter connection** set Base URL `http://mockai:9000/api/v1` and any API key.
   (With a real key, use the real URL and key instead.)
2. Send a customer email to the agent's mailbox and the agent's reply into that mailbox's Sent folder, as `live_agent.py` does, so that
   the conversation ends with an agent message containing "that clears the error". After 1-3 minutes the inquiry becomes **Potentially resolved**.
   (Alternatively, with a real key, hold a real conversation: customer asks, agent explains the fix, customer says it works.)

## 3. Checks

| # | Do this in the Agent | Expected |
|---|---|---|
| 1 | Wait for the pop-up | A pop-up titled **Please confirm: resolved?** appears for that inquiry |
| 2 | Look at the inquiry list | The inquiry is listed with **• Looks resolved - please confirm** |
| 3 | Open the inquiry | Buttons **Confirm Resolved** and **Not Resolved Yet** are both shown |
| 4 | Click **Not Resolved Yet** | Message "Recorded. Work status: ..." (not "Failed"); the inquiry leaves "Looks resolved". In the CMS (Inquiry Supervision → open it) the stage is no longer *Potentially resolved* and the history says the agent said it is not resolved |
| 5 | Open an inquiry that is **not** potentially resolved | **Not Resolved Yet** is **not** shown; **Confirm Resolved** is |
| 6 | Make it potentially resolved again (send another agent reply) and click **Confirm Resolved** | "Recorded. Work status: Completed". The inquiry disappears from the Agent's list. CMS: stage **Resolved**, history shows the confirmation |
| 7 | Wait 5 minutes | No further reminder or escalation pop-up for that inquiry; no escalation email |
| 8 | Click **Confirm Resolved** on the same (now closed) inquiry again if it is still visible, or from a stale window | A readable error ("This inquiry is already closed."), no crash |
| 9 | Stop the API (`docker stop iemas-inq-api`), click either button | A readable "Failed: ..." message, no crash; after `docker start iemas-inq-api` the Agent reconnects and a retry works |
| 10 | Two Agents: from agent2's window try to act on agent1's inquiry (only possible with a stale/injected request) | Rejected: "This Agent's employee does not own this Case." |

Record each result (pass/fail, screenshot) in PROGRESS.md §9 under "Verified through the Windows Agent".

## 4. Known limits to look for

- The status text uses the numeric enum order of the server. The Agent's `CaseWorkStatus` enum was extended with `PotentiallyResolved = 12`; if
  check 2 shows a number or a blank, the Agent build is older than the server.
- The Agent window's own status line in the case window prints `Work: PotentiallyResolved`, which is expected.
