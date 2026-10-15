# Diagrams — Shared Skill Code Distribution

## System Context (C1)

One skill tree serves several consumers: the skills reference shared code, three
distribution routes ship it, a parity harness watches the copies, and the capture
contract owns the behaviour regardless of where the files live.

```mermaid
C4Context
    title Shared skill code distribution — System Context

    Person(author, "Skill author", "Fixes shared logic once; adds consumers that reference it")
    Person(agent, "Agent session", "Runs skill scripts from any installed layout")

    System(tree, "Mimisbrunnr skill tree", "Skills plus one shared scripts folder; WET copies only per external plugin")

    System_Ext(marketplace, "Claude Code marketplace installer", "Installs plugins by manifest skill paths; copies the whole repository")
    System_Ext(npm, "npm registry and launchers", "Ships the allowlisted files; launchers resolve scripts by path")
    System_Ext(parity, "Parity harness", "Fails on drift between the shared source and any WET copy")
    System_Ext(contract, "Capture contract (HLD-002, odin)", "Sole authority the shared code implements; ownership never moves with the files")

    Rel(author, tree, "Fixes once in the shared folder; re-copies outward")
    Rel(agent, tree, "Runs scripts from checkout, plugin cache, or npm layout")
    Rel(tree, marketplace, "Ships via manifest paths plus the shared folder")
    Rel(tree, npm, "Ships via files allowlist")
    Rel(parity, tree, "Compares shared source against every copy")
    Rel(contract, tree, "Owns the behaviour the shared code carries")
```

The tree is the only thing that moves; the contract, the consumers, and the routes stay
where they are.
