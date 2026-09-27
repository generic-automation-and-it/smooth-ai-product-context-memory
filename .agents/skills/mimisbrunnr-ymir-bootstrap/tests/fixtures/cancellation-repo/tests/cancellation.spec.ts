import { cancellationFee } from "../src/cancellation";

const startsAt = new Date("2026-10-03T12:00:00Z");

it("charges a card booking cancelled 47 hours before start", () => {
  expect(cancellationFee({ paymentMethod: "card", startsAt }, new Date("2026-10-01T13:00:00Z"))).toBe(10);
});

it("does not charge a card booking cancelled 48 hours before start", () => {
  expect(cancellationFee({ paymentMethod: "card", startsAt }, new Date("2026-10-01T12:00:00Z"))).toBe(0);
});

it("never charges a voucher booking", () => {
  expect(cancellationFee({ paymentMethod: "voucher", startsAt }, new Date("2026-10-03T11:00:00Z"))).toBe(0);
});
