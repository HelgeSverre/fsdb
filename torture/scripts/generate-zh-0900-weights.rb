# Generate SQL, run it on the pinned MySQL oracle with mysql -NB, then pack its output.
require "zlib"

collation, signature = case ARGV.first
                       when "--accent-sensitive"
                         ARGV.shift
                         ["utf8mb4_0900_as_ci", "FAS1"]
                       when "--case-sensitive"
                         ARGV.shift
                         ["utf8mb4_0900_as_cs", "FCS1"]
                       else
                         ["utf8mb4_zh_0900_as_cs", "FZH1"]
                       end
accent_sensitive = signature == "FAS1"
chinese = signature == "FZH1"

case ARGV.shift
when "queries"
  puts <<~SQL
    SET cte_max_recursion_depth=1200;
    WITH RECURSIVE digit AS (SELECT 0 AS n UNION ALL SELECT n+1 FROM digit WHERE n<1087),
    points AS (SELECT hi.n*1024+lo.n AS cp FROM digit hi CROSS JOIN digit lo WHERE lo.n<1024)
    SELECT cp,HEX(WEIGHT_STRING(CONVERT(CHAR(cp USING utf32) USING utf8mb4) COLLATE #{collation}))
    FROM points WHERE cp NOT BETWEEN 55296 AND 57343 ORDER BY cp;
  SQL
  exit unless chinese

  sequences = %w[重庆 沈阳 藏文]
  ["E", "e"].each do |letter|
    ["\u0300", "\u0301", "\u0304", "\u030C"].each { |mark| sequences << "#{letter}\u0302#{mark}" }
  end
  ["M", "m"].each do |letter|
    ["\u0300", "\u0304", "\u030C"].each { |mark| sequences << "#{letter}#{mark}" }
  end
  sequences.concat(["N\u0304", "n\u0304"])

  sequences.sort.each do |sequence|
    hex = sequence.unpack1("H*").upcase
    puts "SELECT 'C:#{hex}',HEX(WEIGHT_STRING(CONVERT(X'#{hex}' USING utf8mb4) COLLATE #{collation}));"
  end
when "pack"
  dump, destination = ARGV
  abort "Usage: generate-zh-0900-weights.rb pack mysql.tsv output.bin.gz" unless dump && destination
  ranges = []
  explicit = []
  sequences = []
  first = nil
  last = nil
  base_weight = nil
  expected_scalar = 0
  implicit_suffix = accent_sensitive ? "000000200000" : "0000002000000002"

  flush_range = lambda do
    if first
      if last - first >= 3
        ranges << [first, last, base_weight]
      else
        (first..last).each { |cp| explicit << [cp, [base_weight + cp - first].pack("N") + [implicit_suffix].pack("H*")] }
      end
    end
  end

  File.foreach(dump) do |line|
    input, hex = line.split
    abort "Invalid weight at #{input}" unless hex && hex.match?(/\A(?:[0-9A-F]{4})+\z/)
    # Keep the loader's two level delimiters. The second follows the final
    # secondary weight for this collation.
    hex += "0000" if accent_sensitive
    bytes = [hex].pack("H*")
    if input.start_with?("C:")
      sequences << [[input.delete_prefix("C:")].pack("H*"), bytes]
      next
    end

    cp = Integer(input)
    abort "Expected scalar #{expected_scalar}, got #{cp}" unless cp == expected_scalar
    expected_scalar = cp == 0xD7FF ? 0xE000 : cp + 1

    # Implicit weights are two primary words followed by fixed secondary and
    # tertiary weights. Runs encode adjacent primary values as a base and offset.
    implicit = hex.match?(/\A[0-9A-F]{8}#{implicit_suffix}\z/)
    primary = implicit ? bytes.unpack1("N") : nil
    if first && cp == last + 1 && primary == base_weight + cp - first
      last = cp
    else
      flush_range.call
      first = implicit ? cp : nil
      last = cp
      base_weight = primary
      explicit << [cp, bytes] unless implicit
    end
  end
  flush_range.call
  abort "Incomplete oracle dump" unless expected_scalar == 0x110000 && sequences.length == (chinese ? 19 : 0)

  Zlib::GzipWriter.open(destination, Zlib::BEST_COMPRESSION) do |output|
    output.mtime = 0
    output.write(signature)
    output.write([ranges.length, explicit.length, sequences.length].pack("V*"))
    ranges.each { |range| output.write(range.pack("V*")) }
    explicit.each { |cp, key| output.write([cp, key.bytesize].pack("Vv") + key) }
    sequences.each { |text, key| output.write([text.bytesize, key.bytesize].pack("vv") + text + key) }
  end
else
  abort "Usage: generate-zh-0900-weights.rb queries | pack mysql.tsv output.bin.gz"
end
