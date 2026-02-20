import sys
path = r'C:\Users\clawd\DartGameSystem\DartGameAPI\wwwroot\js\dartsmob.js'
data = open(path, encoding='utf-8').read()

# Fix leg-won modal: CSS uses .modal.hidden (display:none), not .modal.show
data = data.replace("modal.classList.remove('show');", "modal.classList.add('hidden');")
data = data.replace("modal.classList.add('show');", "modal.classList.remove('hidden');")

# Also need to start modal hidden when first created
data = data.replace(
    "modal.className = 'modal';",
    "modal.className = 'modal hidden';"
)

open(path, 'w', encoding='utf-8').write(data)
print("DONE")
print("'hidden' occurrences:", data.count("'hidden'"))
