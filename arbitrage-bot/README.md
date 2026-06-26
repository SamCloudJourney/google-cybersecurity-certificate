# Crypto Arbitrage Scanner (Simulation)

A small, honest tool for **detecting** cross-exchange price gaps in crypto and
showing you whether they survive trading fees. It runs in **simulation mode
only** — it reads live public prices and does math. It places **no orders** and
needs **no API keys**.

## Read this before you get excited

Arbitrage is real, but easy retail arbitrage mostly is not. The headline gap
("BTC is $30 cheaper on exchange A!") almost always evaporates once you account
for the real costs of capturing it:

| Cost | Typical size | Why it kills the trade |
|------|-------------|------------------------|
| Taker fees (both legs) | 0.1%–0.6% each | You pay to buy *and* to sell |
| Withdrawal / transfer fees | flat + network | Moving coins between venues isn't free |
| **Transfer latency** | seconds to minutes | The gap is gone before your coins arrive |
| Slippage | grows with size | Big orders move the price against you |
| Spread | always | You buy at the ask, sell at the bid |

The pros who *do* capture these gaps win on speed (co-located servers, sub-ms
latency) and by pre-funding capital on **both** exchanges so they never have to
transfer mid-trade. A Python script polling REST APIs cannot beat them on the
fast gaps. What this tool is good for: **learning the real economics**, spotting
the rare larger dislocations, and building intuition before you ever risk money.

> If anyone promises you a bot with guaranteed arbitrage profits, it's a scam.

## Usage

```bash
pip install requests          # the only dependency

python3 arb_bot.py                          # one scan, BTC/USD
python3 arb_bot.py --symbol ETH/USD         # a different pair
python3 arb_bot.py --watch --interval 10    # poll every 10 seconds
python3 arb_bot.py --min-profit 0.25        # only flag >= 0.25% net edges
python3 arb_bot.py --exchanges kraken,coinbase   # subset of venues
```

### Configuring fees

The defaults are public ballpark **taker** rates and are probably wrong for
your account. Copy and edit:

```bash
cp config.example.json config.json
# edit config.json with YOUR real taker fees
```

Lower fees ⇒ more routes turn profitable. This is the single biggest lever.

## How it models a trade

For every ordered pair of exchanges it assumes the realistic worst case:

```
net % = (sell_bid - buy_ask) / buy_ask  -  taker_fee_buy  -  taker_fee_sell
```

i.e. you **buy at the ask** on one venue and **sell at the bid** on the other,
paying taker fees on both. It deliberately does **not** model withdrawal fees or
transfer time — those make the picture *worse*, not better, so a route that
looks unprofitable here is definitely unprofitable in reality.

## Supported exchanges (public price endpoints)

`coinbase`, `kraken`, `bitstamp`, `binance` (Binance may be geo-blocked from
some regions — the tool skips any venue it can't reach).

## If you ever want to go live (read first)

Execution is intentionally **not** included. Going live responsibly means:

1. Pre-funding capital on **both** exchanges (so you never transfer mid-arb).
2. Real API keys with **trade-only** permission (never withdrawal permission).
3. Modeling slippage against real order-book depth, not just top-of-book.
4. Starting with tiny sizes and reconciling every fill against expectation.
5. Understanding the **legal/tax/KYC** rules in your jurisdiction.

Treat live trading as a separate, much harder project. Prove the edge is real
in simulation across days or weeks first.

## Disclaimer

This is educational software provided as-is, with no warranty. It is not
financial advice. Trading crypto can lose you money. You are responsible for
your own decisions, taxes, and compliance.
