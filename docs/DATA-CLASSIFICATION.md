# Data Classification

| Class | Meaning | Default MCP handling |
|---|---|---|
| PUBLIC | intentionally public | allow |
| INTERNAL | non-public engineering data | allow within authorized project |
| CONFIDENTIAL | sensitive business/application data | minimize + audit |
| PII | personally identifiable information | mask by default |
| SECRET | secret material not necessarily auth credential | redact |
| CREDENTIAL | authentication/authorization material | always redact |
| UNKNOWN | unclassified data | conservative handling |

Classification happens before the output crosses the MCP boundary. Mandatory credential redaction cannot be disabled by prompt or target content.
