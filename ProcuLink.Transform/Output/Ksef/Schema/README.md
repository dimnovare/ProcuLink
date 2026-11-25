# KSeF FA(3) schema (Ministry of Finance, Poland)

The four XSD files here are the official FA(3) invoice schema, `wersjaSchemy="1-0E"`, saved byte-exact
(fetched 2026-11-25) from the Ministry of Finance's form registry:

| File | Source | SHA-256 |
|---|---|---|
| `schemat_FA(3)_v1-0E.xsd` | http://crd.gov.pl/wzor/2025/06/25/13775/schemat.xsd | `b646b6b525f51adf1bb2545f111fc8ca6e7aa6dd2f98948f1667d3695c06d958` |
| `StrukturyDanych_v10-0E.xsd` | http://crd.gov.pl/xml/schematy/dziedzinowe/mf/2022/01/05/eD/DefinicjeTypy/StrukturyDanych_v10-0E.xsd | `1137ce6e3c11c2b9ef3f05e4e72d6dcd6b4fa94908ea558f2ba15de0259bb2aa` |
| `ElementarneTypyDanych_v10-0E.xsd` | http://crd.gov.pl/xml/schematy/dziedzinowe/mf/2022/01/05/eD/DefinicjeTypy/ElementarneTypyDanych_v10-0E.xsd | `8a531cb181d3e298d11b28766655ae91fee2d7851440095932ffc82137ed2be1` |
| `KodyKrajow_v10-0E.xsd` | http://crd.gov.pl/xml/schematy/dziedzinowe/mf/2022/01/05/eD/DefinicjeTypy/KodyKrajow_v10-0E.xsd | `1d41a1b3184188f2d20a51d3afde26204dda182ec5dacf018204dcc9870dc644` |

The same schema is published in the Ministry's KSeF documentation repository,
https://github.com/CIRFMF/ksef-docs, under the MIT licence (`LICENSE-CIRFMF-ksef-docs.txt`, verbatim).
`.gitattributes` keeps these files free of line-ending changes. `Fa3Schema.cs` loads them as embedded
resources and never fetches a `schemaLocation`.
