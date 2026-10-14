# StyleBro's rules for this code (2026-10-11)

Adopting StyleBro (StyleBro.Analyzers, `stylebro-migrate init` then `format`, checked in CI). Its first format
rewrote about 2,700 lines beyond what `init` kept. Member order (BRO1001) and explicit `private` (BRO1404) were off
from the trial. Asked which more to turn off: **summary wording** (BRO1604/BRO1606, "Gets ..." / "Initializes a new
instance ..." on ~100 summaries), **combined declarations** (BRO1142/BRO1114, the exact ports' `int n = h.Length,
i = 0;`) and **braces on multi-line bodies** (BRO1515, nested `fixed` in the SIMD code) are off; **one-line blocks**
(BRO1508, `if (x) y;` on one line) stay on, so those bodies move to their own line.
