from PIL import Image

img = Image.open('task_images/ZP-87.jpg')
width, height = img.size

# Let's crop a 1024x1024 square from the center
new_size = 1024
left = (width - new_size)/2
top = (height - new_size)/2
right = (width + new_size)/2
bottom = (height + new_size)/2

img_cropped = img.crop((left, top, right, bottom))
img_cropped.save('src/PromptQueue.App/Assets/app.ico', format='ICO', sizes=[(256, 256)])
print("Cropped successfully")
