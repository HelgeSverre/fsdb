# Capture the nonidentity BMP weights shared by MySQL's two general_ci collations.
case ARGV.shift
when "queries"
  puts <<~SQL
    SET cte_max_recursion_depth=256;
    WITH RECURSIVE digit AS (SELECT 0 AS n UNION ALL SELECT n+1 FROM digit WHERE n<255),
    points AS (SELECT hi.n*256+lo.n AS cp FROM digit hi CROSS JOIN digit lo),
    letters AS (SELECT cp, CONVERT(CHAR(cp USING utf32) USING utf8mb4) AS letter
                FROM points WHERE cp NOT BETWEEN 55296 AND 57343)
    SELECT cp, HEX(WEIGHT_STRING(letter COLLATE utf8mb4_general_ci)),
           HEX(WEIGHT_STRING(CONVERT(letter USING utf8mb3) COLLATE utf8mb3_general_ci))
    FROM letters ORDER BY cp;
  SQL
when "pack"
  dump = ARGV.shift or abort "Usage: generate-general-fulltext-weights.rb pack mysql.tsv"
  expected = 0
  STDOUT.binmode
  File.foreach(dump) do |line|
    input, mb4, mb3 = line.chomp.split("\t", -1)
    scalar = Integer(input, 10)
    abort "Unexpected scalar #{scalar}" unless scalar == expected
    abort "Invalid or differing weights at #{scalar}" unless mb4&.match?(/\A[0-9A-F]{4}\z/) && mb4 == mb3
    weight = Integer(mb4, 16)
    STDOUT.write([scalar, weight].pack("n2")) if weight != scalar
    expected = scalar == 0xD7FF ? 0xE000 : scalar + 1
  end
  abort "Incomplete BMP capture" unless expected == 0x10000
else
  abort "Usage: generate-general-fulltext-weights.rb queries | pack mysql.tsv"
end
