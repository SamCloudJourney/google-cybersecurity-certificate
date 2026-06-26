#!/usr/bin/env python3
"""
Crypto exchange arbitrage scanner — SIMULATION MODE by default.

What this does:
  - Fetches live BID/ASK prices for a symbol from several public exchange
    REST endpoints (no API keys required for read-only price data).
  - For every ordered pair of exchanges, models the realistic outcome of
    "buy on A, sell on B": you BUY at A's ask, SELL at B's bid, and pay
    taker fees on both legs.
  - Reports the NET spread after fees. A spread is only an opportunity if
    it clears every cost. Most of the time, it won't — that is the honest
    truth of retail arbitrage, and this tool is built to show you that
    rather than hide it.

What this does NOT do:
  - It does NOT place real orders. There is no execution code here, by
    design. Detecting an edge and safely capturing it are very different
    problems (transfer latency, withdrawal limits, KYC, capital on both
    venues, slippage on size). Start by trusting the numbers, not a bot.

Usage:
    python3 arb_bot.py                      # one scan of BTC/USD
    python3 arb_bot.py --symbol ETH/USD     # different symbol
    python3 arb_bot.py --watch --interval 10   # poll every 10s
    python3 arb_bot.py --min-profit 0.2     # only show >= 0.2% net edges
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import time
from dataclasses import dataclass
from datetime import datetime, timezone

try:
    import requests
except ImportError:
    sys.exit("This tool needs the 'requests' library:  pip install requests")


# ---------------------------------------------------------------------------
# Fee model
# ---------------------------------------------------------------------------
# Default *taker* fees (the fee you pay when you cross the spread, which is
# what an arbitrage fill does). These are ballpark public rates and WILL be
# wrong for your account tier — override them in config.json. Withdrawal /
# transfer fees are NOT modeled here and can dwarf the spread; see README.
DEFAULT_TAKER_FEES = {
    "coinbase": 0.0060,   # 0.60%
    "kraken":   0.0026,   # 0.26%
    "bitstamp": 0.0030,   # 0.30%
    "binance":  0.0010,   # 0.10%
}


@dataclass
class Quote:
    exchange: str
    bid: float          # price you can SELL at
    ask: float          # price you can BUY at
    ts: float


# ---------------------------------------------------------------------------
# Exchange adapters — each returns a Quote(bid, ask) or raises.
# All endpoints are public and read-only. Symbol is normalized as "BTC/USD".
# ---------------------------------------------------------------------------
def _split(symbol: str) -> tuple[str, str]:
    base, quote = symbol.upper().split("/")
    return base, quote


def fetch_coinbase(symbol: str, session: requests.Session) -> Quote:
    base, quote = _split(symbol)
    product = f"{base}-{quote}"
    url = f"https://api.exchange.coinbase.com/products/{product}/ticker"
    r = session.get(url, timeout=10)
    r.raise_for_status()
    d = r.json()
    return Quote("coinbase", float(d["bid"]), float(d["ask"]), time.time())


def fetch_kraken(symbol: str, session: requests.Session) -> Quote:
    base, quote = _split(symbol)
    # Kraken uses XBT for BTC and odd pair codes; ask the API to resolve.
    pair = f"{base}{quote}".replace("BTC", "XBT")
    url = f"https://api.kraken.com/0/public/Ticker?pair={pair}"
    r = session.get(url, timeout=10)
    r.raise_for_status()
    d = r.json()
    if d.get("error"):
        raise RuntimeError(f"kraken: {d['error']}")
    result = d["result"]
    key = next(iter(result))             # Kraken returns its own pair key
    t = result[key]
    return Quote("kraken", float(t["b"][0]), float(t["a"][0]), time.time())


def fetch_bitstamp(symbol: str, session: requests.Session) -> Quote:
    base, quote = _split(symbol)
    pair = f"{base}{quote}".lower()
    url = f"https://www.bitstamp.net/api/v2/ticker/{pair}/"
    r = session.get(url, timeout=10)
    r.raise_for_status()
    d = r.json()
    return Quote("bitstamp", float(d["bid"]), float(d["ask"]), time.time())


def fetch_binance(symbol: str, session: requests.Session) -> Quote:
    base, quote = _split(symbol)
    # Binance quotes USDT, not USD. Treat USD≈USDT for scanning (NOT for
    # real execution — USDT can depeg). Map USD -> USDT.
    q = "USDT" if quote == "USD" else quote
    pair = f"{base}{q}"
    url = f"https://api.binance.com/api/v3/ticker/bookTicker?symbol={pair}"
    r = session.get(url, timeout=10)
    r.raise_for_status()
    d = r.json()
    return Quote("binance", float(d["bidPrice"]), float(d["askPrice"]), time.time())


ADAPTERS = {
    "coinbase": fetch_coinbase,
    "kraken": fetch_kraken,
    "bitstamp": fetch_bitstamp,
    "binance": fetch_binance,
}


# ---------------------------------------------------------------------------
# Core
# ---------------------------------------------------------------------------
def load_fees(path: str | None) -> dict[str, float]:
    fees = dict(DEFAULT_TAKER_FEES)
    if path and os.path.exists(path):
        with open(path) as f:
            cfg = json.load(f)
        fees.update(cfg.get("taker_fees", {}))
    return fees


def gather_quotes(symbol: str, exchanges: list[str]) -> tuple[list[Quote], list[str]]:
    session = requests.Session()
    session.headers.update({"User-Agent": "arb-scanner/1.0 (simulation)"})
    quotes, errors = [], []
    for name in exchanges:
        try:
            quotes.append(ADAPTERS[name](symbol, session))
        except Exception as e:  # network/geo/symbol issues are expected
            errors.append(f"{name}: {type(e).__name__}: {e}")
    return quotes, errors


@dataclass
class Opportunity:
    buy_on: str
    sell_on: str
    buy_ask: float
    sell_bid: float
    gross_pct: float     # before fees
    net_pct: float       # after taker fees on both legs


def find_opportunities(quotes: list[Quote], fees: dict[str, float]) -> list[Opportunity]:
    opps = []
    for a in quotes:                      # buy here (pay the ask)
        for b in quotes:                  # sell here (hit the bid)
            if a.exchange == b.exchange:
                continue
            gross = (b.bid - a.ask) / a.ask
            fee = fees.get(a.exchange, 0.0) + fees.get(b.exchange, 0.0)
            net = gross - fee
            opps.append(Opportunity(
                a.exchange, b.exchange, a.ask, b.bid, gross * 100, net * 100,
            ))
    opps.sort(key=lambda o: o.net_pct, reverse=True)
    return opps


def scan(symbol: str, fees: dict[str, float], min_profit: float,
         exchanges: list[str]) -> None:
    now = datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M:%S UTC")
    quotes, errors = gather_quotes(symbol, exchanges)

    print(f"\n=== {symbol}  @ {now}  [SIMULATION — no real orders] ===")
    if quotes:
        print("  Live quotes (bid / ask):")
        for q in sorted(quotes, key=lambda x: x.exchange):
            print(f"    {q.exchange:9s}  bid {q.bid:>12,.2f}   ask {q.ask:>12,.2f}")
    for err in errors:
        print(f"  [skipped] {err}")

    if len(quotes) < 2:
        print("  Need quotes from >=2 exchanges to compare. Nothing to do.")
        return

    opps = find_opportunities(quotes, fees)
    best = opps[0]
    profitable = [o for o in opps if o.net_pct >= min_profit]

    print("\n  Best theoretical route:")
    print(f"    BUY {symbol.split('/')[0]} on {best.buy_on} @ {best.buy_ask:,.2f}"
          f"  ->  SELL on {best.sell_on} @ {best.sell_bid:,.2f}")
    print(f"    gross {best.gross_pct:+.3f}%   |   net after taker fees "
          f"{best.net_pct:+.3f}%")

    if profitable:
        print(f"\n  *** {len(profitable)} route(s) clear your {min_profit:.2f}% "
              f"threshold AFTER fees: ***")
        for o in profitable:
            print(f"    {o.buy_on} -> {o.sell_on}: net {o.net_pct:+.3f}%")
        print("    Reminder: this ignores withdrawal/transfer fees, slippage,")
        print("    and the time to move funds. Verify before trusting it.")
    else:
        print(f"\n  No route clears {min_profit:.2f}% net. This is the normal,"
              " expected result.")


def main() -> None:
    p = argparse.ArgumentParser(description="Crypto arbitrage scanner (simulation).")
    p.add_argument("--symbol", default="BTC/USD", help="e.g. BTC/USD, ETH/USD")
    p.add_argument("--config", default="config.json", help="fee overrides")
    p.add_argument("--min-profit", type=float, default=0.0,
                   help="min net %% to flag as an opportunity")
    p.add_argument("--exchanges", default=",".join(ADAPTERS),
                   help="comma-separated subset of: " + ",".join(ADAPTERS))
    p.add_argument("--watch", action="store_true", help="poll continuously")
    p.add_argument("--interval", type=float, default=15.0, help="seconds between polls")
    args = p.parse_args()

    fees = load_fees(args.config)
    exchanges = [e.strip() for e in args.exchanges.split(",") if e.strip() in ADAPTERS]
    if not exchanges:
        sys.exit("No valid exchanges selected.")

    if not args.watch:
        scan(args.symbol, fees, args.min_profit, exchanges)
        return

    print("Watching... Ctrl-C to stop.")
    try:
        while True:
            scan(args.symbol, fees, args.min_profit, exchanges)
            time.sleep(args.interval)
    except KeyboardInterrupt:
        print("\nStopped.")


if __name__ == "__main__":
    main()
