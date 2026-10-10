# ResMan A/B/C eval (2026-10-10 03:43)

- A (big model, logs only): not run (no answers in evals/in/A)
- B (big model, ResMan evidence): not run (no answers in evals/in/B)
- C (local 4B, ResMan harness): 14/14 passed
- C latency: median 69.7s, max 76.5s (local GPU, nothing sent online)

| Case | A | B | C | C notes |
|---|---|---|---|---|
| demo-memory-hog |  -  |  -  | ok  | tools: get_event_details(3)  |
| no-signature-periodic-stutter |  -  |  -  | ok  | tools: get_resource_history(300) -> get_top_processes()  |
| offline-weak-signal |  -  |  -  | ok  | tools: get_event_details(2) -> get_network_history(6)  |
| offline-isp-not-wifi |  -  |  -  | ok  | tools: get_event_details(1) -> get_network_history(6)  |
| bsod-after-driver |  -  |  -  | ok  | tools: get_event_details(4) -> get_resource_history(30) -> get_event_details(3)  |
| overheating-shutdowns |  -  |  -  | ok  | tools: get_event_details(29) -> get_resource_history(30) -> get_resource_history(60)  |
| failing-drive |  -  |  -  | ok  | tools: get_event_details(3) -> get_resource_history(30) -> get_resource_history(60)  |
| app-update-crashes |  -  |  -  | ok  | tools: get_event_details(3) -> get_resource_history(30) -> get_resource_history(60)  |
| freeze-disk-hog |  -  |  -  | ok  | tools: get_event_details(2) -> get_resource_history(300)  |
| browser-memory |  -  |  -  | ok  | tools: get_event_details(2) -> get_resource_history(300)  |
| slow-boot-startup-apps |  -  |  -  | ok  | tools: get_event_details(3) -> get_resource_history(30) -> get_changes(7) -> get_event_details(2)  |
| nothing-relevant |  -  |  -  | ok  | tools: get_event_details(1) -> get_network_history(6) -> get_findings()  |
| noise-only |  -  |  -  | ok  | tools: get_health_events(48) -> get_resource_history(30) -> get_event_details(no_event_id_provided)  |
| unknown-process-crash-loop |  -  |  -  | ok  | tools: get_event_details(3) -> inspect_binary(WidgetBarHelper)  |
