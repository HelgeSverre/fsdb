# Capture lowercase mappings from the pinned MySQL oracle and emit compact ranges.
case ARGV.shift
when "queries"
  puts <<~SQL
    SET cte_max_recursion_depth=1200;
    WITH RECURSIVE digit AS (SELECT 0 AS n UNION ALL SELECT n+1 FROM digit WHERE n<1087),
    points AS (SELECT hi.n*1024+lo.n AS cp FROM digit hi CROSS JOIN digit lo WHERE lo.n<1024),
    letters AS (SELECT cp,CONVERT(CHAR(cp USING utf32) USING utf8mb4) AS letter FROM points WHERE cp NOT BETWEEN 55296 AND 57343)
    SELECT cp,
           HEX(LEFT(LOWER(CONCAT(letter,_utf8mb4'####') COLLATE utf8mb4_unicode_ci),1)),
           HEX(LEFT(LOWER(CONCAT(letter,_utf8mb4'####') COLLATE utf8mb4_unicode_520_ci),1))
    FROM letters ORDER BY cp;
  SQL
when "pack"
  dump = ARGV.shift or abort "Usage: generate-legacy-fulltext-case.rb pack mysql.tsv"
  tables = [[], []]
  expected = 0
  File.foreach(dump) do |line|
    input, *outputs = line.chomp.split("\t", -1)
    scalar = Integer(input, 10)
    abort "Unexpected input scalar #{scalar}" unless scalar == expected && outputs.length == tables.length
    expected = scalar == 0xD7FF ? 0xE000 : scalar + 1

    outputs.each_with_index do |hex, index|
      abort "Invalid lowercase bytes at #{scalar}" unless hex.match?(/\A(?:[0-9A-F]{2})+\z/)
      target, *rest = [hex].pack("H*").force_encoding("UTF-8").codepoints
      abort "Invalid lowercase scalar at #{scalar}" unless target && rest.empty? && (target <= 0xFFFF) == (scalar <= 0xFFFF)
      tables[index] << [scalar, target - scalar] if target != scalar
    end
  end
  abort "Incomplete lowercase capture" unless expected == 0x110000

  puts "// Generated from MySQL 8.4.11 by torture/scripts/generate-legacy-fulltext-case.rb."
  puts "module internal Fsdb.LegacyFullTextCaseData"
  %w[unicode400 unicode520].zip(tables).each do |name, pairs|
    ranges = []
    pairs.each do |scalar, delta|
      previous = ranges.last
      step = scalar - previous[1] if previous
      if previous && delta == previous[3] && [1, 2].include?(step) && (previous[2].nil? || previous[2] == step)
        previous[1] = scalar
        previous[2] = step
      else
        ranges << [scalar, scalar, nil, delta]
      end
    end
    puts "\nlet #{name} =\n    [|"
    ranges.each_slice(3) do |row|
      puts "        " + row.map { |first, last, step, delta| format("0x%04X, 0x%04X, %d, %d", first, last, step || 1, delta) }.join("; ") + ";"
    end
    puts "    |]"
  end
else
  abort "Usage: generate-legacy-fulltext-case.rb queries | pack mysql.tsv"
end
