# I/O, locks and transactions

## I/O outcomes

After `B.IO.HANDLER`, distinguish an ordinary I/O error in `E.REC`, lock contention (normally code `2`), an expected or unexpected missing record through `IO$READ.ELSE`, and success. Set `ERROR.MCODE` before invoking the relevant check macro. A missing record is not automatically an error; decide from the business requirement.

## Lock lifecycle

- A successful `READU` or `READVU` acquires an update lock.
- A successful corresponding `WRITE` or `WRITEV` releases that lock.
- Do not add a redundant release after the successful write.
- If execution does not reach the write, explicitly `RELEASE <file>,<key>`.
- Check every `RETURN`, `CONTINUE`, `EXIT`, error macro and subordinate call after lock acquisition.
- If multiple locks are held, release every outstanding lock, preferably in reverse acquisition order.
- A lock-conflict branch did not acquire the requested lock; return a clear business or error response as appropriate.
- Never clear `E.REC` on lock contention without setting another signal that prevents unlocked processing.
- Track soft locks separately from UniVerse record locks and clean up both.
- Cleanup must not recursively re-enter the same failing cleanup path.

Use a lock ledger during review:

| Event | State |
|---|---|
| successful `READU` | acquired |
| lock contention | not acquired |
| successful `WRITE` | released |
| explicit `RELEASE` | released |
| early exit while acquired | defect |

## Transactions

- Perform validation and non-mutating preparation before the transaction when practical.
- Place logically atomic writes between `BEGIN TRANSACTION` and `END TRANSACTION`.
- After every fallible operation inside the transaction, use the approved rollback check such as `IF$L.ERROR.ROLLBACK`.
- Reach `COMMIT` only when no unresolved error remains; otherwise `ROLLBACK`.
- Avoid slow calls, user interaction and unnecessary external work inside a transaction.
- Verify lock cleanup separately from data rollback. Do not assume rollback releases a lock acquired before the transaction without platform evidence.
- Release outstanding locks after transaction completion or on early rollback paths as required.

Use a transaction ledger listing each write and whether all earlier writes are rolled back when it fails. Flag partial-update risk when related writes are not protected atomically.
