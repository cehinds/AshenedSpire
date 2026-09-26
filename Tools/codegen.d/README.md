# Codegen fragments

Each file here adds `enums`, `keySets`, `numberSets` or `stringTables` to `Tools/codegen.config.json`, so parallel feature streams can own their generated keys without conflicting edits. Files merge in name order; a duplicate identifier fails codegen.
