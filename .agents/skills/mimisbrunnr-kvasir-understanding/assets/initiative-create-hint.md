Create it first, then re-run:
  echo '{"name": "{{initiative}}", "status": "active"}' | python3 -B {{write_client}} upsert-initiative
