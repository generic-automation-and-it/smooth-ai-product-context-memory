export type Booking = {
  paymentMethod: "card" | "voucher";
  startsAt: Date;
};

export function cancellationFee(booking: Booking, cancelledAt: Date): number {
  if (booking.paymentMethod === "voucher") return 0;

  const hoursUntilStart = (booking.startsAt.getTime() - cancelledAt.getTime()) / 3_600_000;
  return hoursUntilStart < 48 ? 10 : 0;
}
