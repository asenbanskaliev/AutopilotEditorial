# VS-060 RED Evidence

At slice start the repository had an approved ScenePlan lifecycle but no durable scene-generation aggregate.

Missing behaviors:

- no causal binding from approved ScenePlan to generated scene;
- no provider/model/prompt/context invocation record;
- no append-only attempts or failure evidence;
- no generated content digest;
- no submission and approval state machine;
- no exactly-once `editorial.scene.approved` Outbox event;
- no restart, idempotency or conflict journey.

The contracts and migration intentionally precede `SqliteSceneGenerationStore` and the cumulative integration journey. Therefore VS-060 remains RED until those behaviors execute against real SQLite and all gates pass.
