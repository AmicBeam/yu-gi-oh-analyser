# Yu-Gi-Oh! Card Database CLI

`ygo-db-cli` queries the MyCard database by passcode, name, archetype, or YDK file and exports YAML, JSON, or CSV. It uses Git to clone or refresh card data in `$XDG_CONFIG_HOME/ygo-db` or `~/.config/ygo-db`.

Run the CLI with the Go 1.26.1 toolchain from this directory.

```sh
go run ./cmd/ygo-db-cli --help
go run ./cmd/ygo-db-cli get-card-by-id 14558127 --lang zh-CN --format yaml
```

See the [card database skill](../skills/ygo-db/SKILL.md) for YDK export, pagination, and language examples.

## Disclaimer

Yu-Gi-Oh! is a trademark of Konami Digital Entertainment. This project is not affiliated with, endorsed, sponsored, or specifically approved by Konami Digital Entertainment. All product and company names are trademarks or registered trademarks of their respective holders.

## License

Apache 2.0
