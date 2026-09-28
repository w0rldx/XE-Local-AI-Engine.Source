# Ranked ballots and rank aggregation

In a ranked election every voter hands in a ranked list of candidates. Combining many ranked lists into one result is
called rank aggregation, and there is no single best way to do it.

## Borda count

The Borda count gives each candidate points by rank: with n candidates, the first rank earns n minus one points, the
second rank earns n minus two, and so on. The candidate with the highest total wins. It rewards broadly liked
candidates over polarising ones.

## Instant runoff

Instant runoff counts first ranks only. The candidate with the fewest first-rank votes is eliminated, and those
ballots move to their next rank. The count repeats until one candidate holds a majority.

## Reciprocal scoring

Some sports rankings use a reciprocal rank score, where the first rank counts one, the second rank a half, the third
a third. A constant can be added to the rank to flatten the gap between the top ranks. None of these methods looks at
margins of victory; they only see rank positions in each ranked list.
